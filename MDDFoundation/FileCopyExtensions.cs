// File: FileCopyExtensions.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public enum FileCopyHashMode
    {
        NoHash = 0,
        FastNativeHashWithResumeReread = 1,
        SlowStatefulHashForWanResume = 2,
        NoHashOnResume = 3
    }

    public static class FileCopyExtensions
    {
        // --------------------------------------------------------------------
        // Public API
        // --------------------------------------------------------------------

        public const string CopyToAsyncSequentialReadChunkedVersion = "CopyToAsyncSequentialReadChunked v0.9-hashpipeline";
        private const int FileRelayStreamBufferSize = 4096;
        private static readonly ConcurrentBag<byte[]> ChunkBufferPool = new ConcurrentBag<byte[]>();

        /// <summary>
        /// Single-destination overload (wraps multicast).
        /// </summary>
        /// 
        public static Task<FileCopyProgress> CopyToAsync(
            this FileInfo file,
            FileInfo destination,
            bool overwrite,
            CancellationToken token,
            bool MoveFile = false,
            Action<FileCopyProgress>? progresscallback = null,
            TimeSpan progressreportinterval = default,
            bool computehash = false,
            bool resumable = true,
            int bufferSize = 1024 * 1024,
            double maxUsage = 1)
        {
            return file.CopyToAsync(
                [destination],
                overwrite,
                token,
                MoveFile,
                progresscallback,
                progressreportinterval,
                computehash,
                resumable,
                bufferSize,
                maxUsage);
        }

        /// <summary>
        /// Multicast resumable copy. The source is read once, sequentially, while chunks are written
        /// to all destinations with bounded parallelism.
        ///
        /// Deterministic per-destination temp + state:
        ///   tmp   = destination.FullName + ".tmp"
        ///   state = destination.FullName + ".tmp.chunks.state"
        ///
        /// Hashing:
        /// - Completed chunks are tracked with XxHash3 chunk hashes for resumability.
        /// - FileCopyProgress.Hash is the legacy SHA1(file) hash when requested.
        /// - FastNativeHashWithResumeReread uses platform SHA1 for the normal hot path and
        ///   rereads the source only if a resumed copy could not hash the whole file in one pass.
        /// - SlowStatefulHashForWanResume uses the slower exportable SHA1 implementation so a
        ///   WAN/expensive-source resume can continue hashing without rereading the prefix.
        /// - NoHashOnResume computes SHA1 only for a fresh copy; resumed copies may finish with
        ///   Hash empty rather than rereading an expensive source.
        ///
        /// Implementation note:
        /// - Three-stage pipeline running on dedicated threads:
        ///     reader (sync source I/O) -> hasher (SHA1 + per-chunk XxHash3) -> workers
        ///     (sync destination writes). Each stage processes chunks in order. Bounded
        ///     BlockingCollections between stages give backpressure.
        /// - All I/O is synchronous. On .NET Framework 4.8, async FileStream over SMB
        ///   consistently under-performs sync I/O (we measured both reads and writes losing
        ///   30-50% throughput). Sync I/O matches robocopy/CopyFileEx behavior. .NET 8 async
        ///   I/O doesn't have the same penalty, but this code is shared with .NET Framework
        ///   callers so sync is the default for both runtimes; the pipeline gets its
        ///   read/hash/write overlap from thread-level parallelism instead.
        /// </summary>
        public static Task<FileCopyProgress> CopyToAsync(
            this FileInfo file,
            FileInfo[] destinations,
            bool overwrite,
            CancellationToken token,
            bool MoveFile = false,
            Action<FileCopyProgress>? progresscallback = null,
            TimeSpan progressreportinterval = default,
            bool computehash = false,
            bool resumable = true,
            int bufferSize = 1024 * 1024, // 1MB
            double maxUsage = 1)
        {
            var copyprogress = new FileCopyProgress
            {
                FileName = file.Name,
                Callback = progresscallback,
                ProgressReportInterval = progressreportinterval,
                SourceFile = file,
                Destinations = destinations,
                Overwrite = overwrite,
                Token = token,
                MoveFile = MoveFile,
                HashMode = computehash ? FileCopyHashMode.FastNativeHashWithResumeReread : FileCopyHashMode.NoHash,
                Resumable = resumable,
                BufferSize = bufferSize,
                MaxUsage = maxUsage
            };
            return CopyToAsync(copyprogress);
        }

        public static Task<FileCopyProgress> CopyToAsync(FileCopyProgress copyprogress)
        {
            return CopyToAsyncSequentialReadChunked(copyprogress);
        }

        public static async Task<FileCopyProgress> CopyToAsyncSequentialReadChunked(FileCopyProgress copyprogress)
        {
            if (copyprogress == null) throw new ArgumentNullException(nameof(copyprogress));
            copyprogress.Stopwatch = Stopwatch.StartNew();
            if (copyprogress.SourceFile == null) throw new ArgumentException("copyprogress.SourceFile must be set to the source file.", nameof(copyprogress));
            copyprogress.FileName = copyprogress.SourceFile.Name;

            if (copyprogress.Destinations == null) throw new ArgumentNullException(nameof(copyprogress.Destinations));
            if (copyprogress.Destinations.Length == 0) throw new ArgumentException("At least one destination is required.", nameof(copyprogress.Destinations));
            if (!copyprogress.SourceFile.Exists) throw new IOException($"Source file {copyprogress.SourceFile.FullName} does not exist");

            copyprogress.FileSizeBytes = copyprogress.SourceFile.Length;
            if (copyprogress.BufferSize <= 0) copyprogress.BufferSize = 1024 * 1024;
            if (copyprogress.ChunkSizeBytes <= 0) copyprogress.ChunkSizeBytes = 50L * 1024 * 1024;
            if (copyprogress.ParallelChunks <= 0) copyprogress.ParallelChunks = 4;
            if (copyprogress.PipelineBufferCount <= 0) copyprogress.PipelineBufferCount = Math.Max(1, copyprogress.ParallelChunks);
            if (copyprogress.ProgressReportInterval == default) copyprogress.ProgressReportInterval = TimeSpan.FromSeconds(1);

            var runtime = RuntimeInformation.FrameworkDescription.Replace(";", ",");
            var arch = RuntimeInformation.ProcessArchitecture;
            var hashMode = copyprogress.HashMode;
            var computeHash = hashMode != FileCopyHashMode.NoHash;
            copyprogress.DiagnosticInfo = $"{CopyToAsyncSequentialReadChunkedVersion}; runtime={runtime}; arch={arch}; io=sync; pipeline=read+hash+write; chunk={copyprogress.ChunkSizeBytes / 1024 / 1024}MiB; parallel={copyprogress.ParallelChunks}; buffer={copyprogress.BufferSize / 1024}KiB; queuedChunks={copyprogress.PipelineBufferCount}; streamBuffer={FileRelayStreamBufferSize / 1024}KiB; stateFlush={copyprogress.ChunkStateFlushInterval.TotalSeconds:N0}s; fullStateFlush={copyprogress.FullFlushChunkState}; verify={copyprogress.VerifyChunkWrites}; hashMode={hashMode}";

            if (copyprogress.Destinations.Any(d => d == null)) throw new ArgumentException("copyprogress.Destinations contains a null entry.", nameof(copyprogress.Destinations));
            var dup = copyprogress.Destinations.GroupBy(d => d.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"Duplicate destination path: {dup.Key}", nameof(copyprogress.Destinations));

            foreach (var d in copyprogress.Destinations)
            {
                if (d.Directory == null || !d.Directory.Exists)
                    throw new IOException($"Destination folder {d.DirectoryName} does not exist or is not available");

                if (d.Exists && !copyprogress.Overwrite)
                    throw new IOException($"Destination file {d.FullName} exists (and overwrite was not specified)");
            }

            copyprogress.UpdateAndMaybeCallback(0);

            var targets = copyprogress.Destinations.Select(d => new CopyTarget(d)).ToArray();
            var statePaths = targets.Select(t => ChunkedCopyStatePath(t)).ToArray();
            var totalChunks = checked((int)((copyprogress.FileSizeBytes + copyprogress.ChunkSizeBytes - 1) / copyprogress.ChunkSizeBytes));
            var states = new ChunkedCopyState[targets.Length];

            bool toolatetocancel = false;
            object stateLock = new object();
            object progressLock = new object();
            long reportedBytes = 0;
            var chunkBytesConfirmed = new long[totalChunks];
            var chunkDone = new bool[totalChunks];
            var stateFlushTimer = Stopwatch.StartNew();
            var diagnostics = new ChunkedCopyDiagnostics(targets.Length);
            var hashQueue = new BlockingCollection<HashWork>(copyprogress.PipelineBufferCount);
            var workQueue = new BlockingCollection<SequentialChunkWork>(copyprogress.PipelineBufferCount);
            byte[] finalWholeHash = Array.Empty<byte>();
            byte[]? sha1ResumeState = null;
            int sha1ResumeChunkCount = 0;
            int sha1CommittedChunkCount = 0;
            byte[][]? sha1StateAfterChunk = hashMode == FileCopyHashMode.SlowStatefulHashForWanResume ? new byte[totalChunks][] : null;

            try
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (!copyprogress.Resumable)
                    {
                        SafeDeleteFile(targets[i].TempPath);
                        SafeDeleteFile(targets[i].StatePath);
                        SafeDeleteFile(statePaths[i]);
                    }

                    var tempInfo = new FileInfo(targets[i].TempPath);
                    var canResumeTarget = copyprogress.Resumable && tempInfo.Exists && tempInfo.Length == copyprogress.FileSizeBytes;

                    states[i] = canResumeTarget
                        ? ChunkedCopyStateFile.TryRead(statePaths[i], copyprogress.SourceFile, copyprogress.ChunkSizeBytes, totalChunks)
                            ?? ChunkedCopyState.Create(copyprogress.SourceFile, copyprogress.ChunkSizeBytes, totalChunks)
                        : ChunkedCopyState.Create(copyprogress.SourceFile, copyprogress.ChunkSizeBytes, totalChunks);

                    using (var temp = new FileStream(
                        targets[i].TempPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        FileRelayStreamBufferSize,
                        FileOptions.None))
                    {
                        if (temp.Length != copyprogress.FileSizeBytes)
                            temp.SetLength(copyprogress.FileSizeBytes);
                    }
                }

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    var hash = FindConfirmedChunkHash(states, chunkIndex);
                    if (hash != null)
                    {
                        chunkDone[chunkIndex] = true;
                        chunkBytesConfirmed[chunkIndex] = ChunkLength(chunkIndex, copyprogress.FileSizeBytes, copyprogress.ChunkSizeBytes);
                    }
                }

                if (hashMode == FileCopyHashMode.SlowStatefulHashForWanResume && copyprogress.Resumable)
                {
                    sha1ResumeChunkCount = FindConfirmedSha1Prefix(states, chunkDone, out sha1ResumeState);
                    sha1CommittedChunkCount = sha1ResumeChunkCount;

                    if (sha1ResumeChunkCount > 0 && sha1ResumeState != null)
                    {
                        for (int i = 0; i < states.Length; i++)
                            states[i].SetSha1State(sha1ResumeChunkCount, sha1ResumeState);
                    }
                }

                copyprogress.BytesStart = chunkBytesConfirmed.Sum();
                copyprogress.BytesCopied = copyprogress.BytesStart;
                reportedBytes = copyprogress.BytesCopied;
                if (copyprogress.BytesCopied > 0)
                    copyprogress.UpdateAndMaybeCallback(0);

                void ReportProgress()
                {
                    lock (progressLock)
                    {
                        var confirmed = chunkBytesConfirmed.Sum();
                        if (confirmed > reportedBytes)
                        {
                            copyprogress.UpdateAndMaybeCallback(confirmed - reportedBytes);
                            reportedBytes = confirmed;
                        }
                    }
                }

                void UpdateSha1PrefixState()
                {
                    if (hashMode != FileCopyHashMode.SlowStatefulHashForWanResume || sha1StateAfterChunk == null) return;

                    var prefix = FindContiguousDonePrefix(chunkDone);
                    if (prefix <= sha1CommittedChunkCount) return;

                    var blob = sha1StateAfterChunk[prefix - 1];
                    if (blob == null) return;

                    sha1CommittedChunkCount = prefix;
                    for (int i = 0; i < states.Length; i++)
                        states[i].SetSha1State(prefix, blob);
                }

                void FlushChunkStates()
                {
                    if (!copyprogress.Resumable) return;
                    for (int i = 0; i < states.Length; i++)
                        ChunkedCopyStateFile.WriteAtomic(statePaths[i], states[i], copyprogress.FullFlushChunkState);
                    stateFlushTimer.Restart();
                }

                using var copyCts = CancellationTokenSource.CreateLinkedTokenSource(copyprogress.Token);

                // Three-stage pipeline: reader (sync source I/O) -> hasher (SHA1 + per-chunk
                // XxHash3) -> workers (sync destination writes). Each stage runs on its own
                // thread, so the wall-clock cost is the slowest stage rather than the sum.
                // Bounded BlockingCollections between stages provide backpressure when any
                // downstream stage falls behind the source.
                var readerTask = Task.Run(() =>
                {
                    try
                    {
                        using var source = new FileStream(
                            copyprogress.SourceFile.FullName,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            copyprogress.BufferSize,
                            FileOptions.SequentialScan);

                        // When the stateful resume mode pre-loaded a partial SHA1 state into the
                        // hasher, skip past the chunks whose state is already captured.
                        var firstChunkIndex = 0;
                        if (computeHash && hashMode == FileCopyHashMode.SlowStatefulHashForWanResume
                            && sha1ResumeChunkCount > 0 && sha1ResumeState != null)
                        {
                            firstChunkIndex = sha1ResumeChunkCount;
                            source.Seek(ChunkOffset(firstChunkIndex, copyprogress.ChunkSizeBytes), SeekOrigin.Begin);
                        }

                        for (int chunkIndex = firstChunkIndex; chunkIndex < totalChunks; chunkIndex++)
                        {
                            copyCts.Token.ThrowIfCancellationRequested();
                            var offset = ChunkOffset(chunkIndex, copyprogress.ChunkSizeBytes);
                            var length = ChunkLength(chunkIndex, copyprogress.FileSizeBytes, copyprogress.ChunkSizeBytes);
                            var kept = !chunkDone[chunkIndex];

                            // Always rent a buffer: the hasher needs the bytes even for
                            // already-done chunks when SHA1 is being computed.
                            var chunkBuffer = RentChunkBuffer(checked((int)length));
                            var totalRead = 0;
                            while (totalRead < length)
                            {
                                var readSize = (int)Math.Min(copyprogress.BufferSize, length - totalRead);
                                var readTicks = Stopwatch.GetTimestamp();
                                var n = source.Read(chunkBuffer, totalRead, readSize);
                                diagnostics.AddRead(Stopwatch.GetTimestamp() - readTicks);
                                if (n == 0) throw new EndOfStreamException("Unexpected end of file during sequential chunked copy.");
                                totalRead += n;
                            }

                            hashQueue.Add(new HashWork(chunkIndex, offset, totalRead, chunkBuffer, kept), copyCts.Token);
                            diagnostics.SamplePeakHeap();
                        }
                    }
                    finally
                    {
                        hashQueue.CompleteAdding();
                    }
                }, copyCts.Token);

                // Belt-and-suspenders: if the reader transitions to faulted/cancelled before its
                // body's finally runs (JIT exception, pre-execution cancellation, etc.) the hasher
                // would block forever on GetConsumingEnumerable. Make sure CompleteAdding fires
                // no matter how the reader ends.
                _ = readerTask.ContinueWith(
                    _ => { try { hashQueue.CompleteAdding(); } catch { } },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var hasherTask = Task.Run(() =>
                {
                    try
                    {
                        Sha1Stateful? statefulWholeSha = null;
                        HashAlgorithm? nativeWholeSha = null;

                        if (computeHash)
                        {
                            if (hashMode == FileCopyHashMode.SlowStatefulHashForWanResume)
                            {
                                statefulWholeSha = new Sha1Stateful();
                                if (sha1ResumeChunkCount > 0 && sha1ResumeState != null)
                                    statefulWholeSha.ImportState(sha1ResumeState);
                                else
                                    statefulWholeSha.Reset();
                            }
                            else if (hashMode == FileCopyHashMode.FastNativeHashWithResumeReread ||
                                     (hashMode == FileCopyHashMode.NoHashOnResume && copyprogress.BytesStart == 0))
                            {
                                nativeWholeSha = SHA1.Create();
                            }
                        }

                        try
                        {
                            foreach (var hw in hashQueue.GetConsumingEnumerable(copyCts.Token))
                            {
                                var hashTicks = Stopwatch.GetTimestamp();
                                statefulWholeSha?.Update(hw.Buffer, 0, hw.Count);
                                nativeWholeSha?.TransformBlock(hw.Buffer, 0, hw.Count, null, 0);

                                if (hw.Kept)
                                {
                                    var chunkXxh = new System.IO.Hashing.XxHash3();
                                    chunkXxh.Append(new ReadOnlySpan<byte>(hw.Buffer, 0, hw.Count));
                                    var chunkHash = chunkXxh.GetHashAndReset();
                                    diagnostics.AddHash(Stopwatch.GetTimestamp() - hashTicks);

                                    workQueue.Add(new SequentialChunkWork(hw.ChunkIndex, hw.Offset, hw.Count, hw.Buffer, chunkHash), copyCts.Token);
                                    diagnostics.SamplePeakHeap();
                                }
                                else
                                {
                                    diagnostics.AddHash(Stopwatch.GetTimestamp() - hashTicks);
                                    // Already-done chunk: workers don't need it, return the buffer to the pool.
                                    ReturnChunkBuffer(hw.Buffer);
                                }

                                if (statefulWholeSha != null && sha1StateAfterChunk != null)
                                {
                                    sha1StateAfterChunk[hw.ChunkIndex] = statefulWholeSha.ExportState();
                                    if (chunkDone[hw.ChunkIndex])
                                    {
                                        lock (stateLock)
                                        {
                                            UpdateSha1PrefixState();
                                        }
                                    }
                                }
                            }

                            if (statefulWholeSha != null)
                            {
                                var finalHashTicks = Stopwatch.GetTimestamp();
                                finalWholeHash = statefulWholeSha.FinalizeHash();
                                diagnostics.AddHash(Stopwatch.GetTimestamp() - finalHashTicks);
                            }
                            else if (nativeWholeSha != null)
                            {
                                var finalHashTicks = Stopwatch.GetTimestamp();
                                nativeWholeSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                finalWholeHash = nativeWholeSha.Hash ?? Array.Empty<byte>();
                                diagnostics.AddHash(Stopwatch.GetTimestamp() - finalHashTicks);
                            }
                        }
                        finally
                        {
                            nativeWholeSha?.Dispose();
                        }
                    }
                    finally
                    {
                        workQueue.CompleteAdding();
                    }
                }, copyCts.Token);

                _ = hasherTask.ContinueWith(
                    _ => { try { workQueue.CompleteAdding(); } catch { } },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var workers = Enumerable.Range(0, Math.Min(copyprogress.ParallelChunks, Math.Max(1, totalChunks))).Select(_ => Task.Run(async () =>
                {
                    try
                    {
                        foreach (var work in workQueue.GetConsumingEnumerable(copyCts.Token))
                        {
                            try
                            {
                                await WriteOrderedChunkToTargetsAsync(targets, work.Offset, work.Buffer, work.Count, copyprogress.BufferSize, copyprogress.VerifyChunkWrites, work.Hash, diagnostics, copyCts.Token).ConfigureAwait(false);
                                var hashText = Convert.ToBase64String(work.Hash);

                                lock (stateLock)
                                {
                                    for (int i = 0; i < states.Length; i++)
                                        states[i].ChunkHashes[work.ChunkIndex] = hashText;

                                    chunkDone[work.ChunkIndex] = true;
                                    chunkBytesConfirmed[work.ChunkIndex] = work.Count;
                                    UpdateSha1PrefixState();

                                    if (stateFlushTimer.Elapsed >= copyprogress.ChunkStateFlushInterval)
                                        FlushChunkStates();
                                }

                                ReportProgress();
                            }
                            finally
                            {
                                ReturnChunkBuffer(work.Buffer);
                                diagnostics.SamplePeakHeap();
                            }
                        }
                    }
                    catch
                    {
                        copyCts.Cancel();
                        throw;
                    }
                }, copyCts.Token)).ToArray();

                await Task.WhenAll(workers.Concat(new[] { readerTask, hasherTask })).ConfigureAwait(false);

                if (computeHash &&
                    finalWholeHash.Length == 0 &&
                    hashMode == FileCopyHashMode.FastNativeHashWithResumeReread &&
                    copyprogress.BytesStart > 0)
                {
                    var hashTicks = Stopwatch.GetTimestamp();
                    finalWholeHash = ComputeWholeFileSha1(copyprogress.SourceFile.FullName, copyprogress.BufferSize, copyprogress.Token);
                    diagnostics.AddHash(Stopwatch.GetTimestamp() - hashTicks);
                }

                copyprogress.Token.ThrowIfCancellationRequested();
                lock (stateLock)
                {
                    FlushChunkStates();
                }
                toolatetocancel = true;

                foreach (var target in targets)
                {
                    if (target.Destination.Exists) target.Destination.Delete();
                }

                foreach (var target in targets)
                {
                    var tmpFi = new FileInfo(target.TempPath);
                    tmpFi.Refresh();
                    if (!tmpFi.Exists)
                        throw new IOException($"CopyToAsyncSequentialReadChunked: expected temp file missing: {tmpFi.FullName}");

                    tmpFi.MoveTo(target.Destination.FullName);
                    target.Destination.Refresh();
                    int retries = 0;
                    while (!target.Destination.Exists && retries < 6)
                    {
                        await Task.Delay(100, copyprogress.Token).ConfigureAwait(false);
                        target.Destination.Refresh();
                        retries++;
                    }
                    if (!target.Destination.Exists)
                        throw new Exception($"CopyToAsyncSequentialReadChunked: file {copyprogress.SourceFile.Name} - copy completed, but it did not appear in the destination '{target.Destination.FullName}'");
                }

                foreach (var target in targets)
                {
                    File.SetCreationTime(target.Destination.FullName, copyprogress.SourceFile.CreationTime);
                    File.SetLastWriteTime(target.Destination.FullName, copyprogress.SourceFile.LastWriteTime);
                    File.SetLastAccessTime(target.Destination.FullName, copyprogress.SourceFile.LastAccessTime);
                    SafeDeleteFile(ChunkedCopyStatePath(target));
                }

                copyprogress.BytesCopied = copyprogress.FileSizeBytes;
                copyprogress.Hash = computeHash ? finalWholeHash : Array.Empty<byte>();

                if (copyprogress.MoveFile) copyprogress.SourceFile.Delete();

                var endHeapMiB = GC.GetTotalMemory(false) / 1024 / 1024;
                var poolBuffers = ChunkBufferPool.ToArray();
                var poolMiB = poolBuffers.Sum(b => (long)b.Length) / 1024 / 1024;
                copyprogress.DiagnosticSummary = $"{diagnostics}; endHeap={endHeapMiB}MiB; pool={poolBuffers.Length}buf/{poolMiB}MiB";
                copyprogress.IsCompleted = true;
                copyprogress.Callback?.Invoke(copyprogress);
                return copyprogress;
            }
            catch (OperationCanceledException)
            {
                copyprogress.Cancelled = true;
                copyprogress.IsCompleted = false;
                copyprogress.Callback?.Invoke(copyprogress);
                throw;
            }
            finally
            {
                hashQueue.Dispose();
                workQueue.Dispose();

                if (!toolatetocancel && !copyprogress.Resumable)
                {
                    foreach (var target in targets)
                    {
                        SafeDeleteFile(target.TempPath);
                        SafeDeleteFile(target.StatePath);
                        SafeDeleteFile(ChunkedCopyStatePath(target));
                    }
                }
            }
        }

        public static async Task<FileCopyProgress> CopyToAsyncWindowsNative(FileCopyProgress copyprogress)
        {
            if (copyprogress == null) throw new ArgumentNullException(nameof(copyprogress));
            copyprogress.Stopwatch = Stopwatch.StartNew();
            if (copyprogress.SourceFile == null) throw new ArgumentException("copyprogress.SourceFile must be set to the source file.", nameof(copyprogress));
            copyprogress.FileName = copyprogress.SourceFile.Name;

            if (copyprogress.Destinations == null) throw new ArgumentNullException(nameof(copyprogress.Destinations));
            if (copyprogress.Destinations.Length == 0) throw new ArgumentException("At least one destination is required.", nameof(copyprogress.Destinations));
            if (!copyprogress.SourceFile.Exists) throw new IOException($"Source file {copyprogress.SourceFile.FullName} does not exist");
            if (copyprogress.ComputeHash) throw new NotSupportedException("CopyToAsyncWindowsNative does not compute hashes yet.");

            copyprogress.FileSizeBytes = copyprogress.SourceFile.Length;
            if (copyprogress.ProgressReportInterval == default) copyprogress.ProgressReportInterval = TimeSpan.FromSeconds(1);

            if (copyprogress.Destinations.Any(d => d == null)) throw new ArgumentException("copyprogress.Destinations contains a null entry.", nameof(copyprogress.Destinations));
            var dup = copyprogress.Destinations.GroupBy(d => d.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"Duplicate destination path: {dup.Key}", nameof(copyprogress.Destinations));

            foreach (var destination in copyprogress.Destinations)
            {
                if (destination.Directory == null || !destination.Directory.Exists)
                    throw new IOException($"Destination folder {destination.DirectoryName} does not exist or is not available");

                if (destination.Exists && !copyprogress.Overwrite)
                    throw new IOException($"Destination file {destination.FullName} exists (and overwrite was not specified)");
            }

            copyprogress.UpdateAndMaybeCallback(0);

            var targets = copyprogress.Destinations.Select(d => new CopyTarget(d)).ToArray();
            foreach (var target in targets)
            {
                SafeDeleteFile(target.TempPath);
                SafeDeleteFile(target.StatePath);
            }

            var destinationBytes = new long[targets.Length];
            long reportedBytes = 0;
            object progressLock = new object();
            bool completed = false;

            void ReportProgress()
            {
                lock (progressLock)
                {
                    long minWritten = destinationBytes.Min();
                    if (minWritten > reportedBytes)
                    {
                        copyprogress.UpdateAndMaybeCallback(minWritten - reportedBytes);
                        reportedBytes = minWritten;
                    }
                }
            }

            try
            {
                var flags = COPY_FILE_NO_BUFFERING;
                var copyTasks = targets.Select((target, destinationIndex) => Task.Run(() =>
                {
                    int cancel = 0;
                    CopyProgressRoutine progress = (totalFileSize, totalBytesTransferred, streamSize, streamBytesTransferred, streamNumber, callbackReason, sourceFile, destinationFile, data) =>
                    {
                        if (copyprogress.Token.IsCancellationRequested)
                        {
                            cancel = 1;
                            return CopyProgressResult.PROGRESS_CANCEL;
                        }

                        Interlocked.Exchange(ref destinationBytes[destinationIndex], totalBytesTransferred);
                        ReportProgress();

                        return CopyProgressResult.PROGRESS_CONTINUE;
                    };

                    try
                    {
                        if (!CopyFileEx(copyprogress.SourceFile.FullName, target.TempPath, progress, IntPtr.Zero, ref cancel, flags))
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (cancel != 0 || copyprogress.Token.IsCancellationRequested || error == ERROR_REQUEST_ABORTED)
                                throw new OperationCanceledException(copyprogress.Token);

                            throw new Win32Exception(error, $"CopyFileEx failed copying '{copyprogress.SourceFile.FullName}' to '{target.TempPath}'");
                        }
                    }
                    finally
                    {
                        GC.KeepAlive(progress);
                    }
                }, copyprogress.Token)).ToArray();

                await Task.WhenAll(copyTasks).ConfigureAwait(false);

                copyprogress.Token.ThrowIfCancellationRequested();

                foreach (var target in targets)
                {
                    if (target.Destination.Exists) target.Destination.Delete();
                }

                foreach (var target in targets)
                {
                    var tmpFi = new FileInfo(target.TempPath);
                    tmpFi.Refresh();
                    if (!tmpFi.Exists)
                        throw new IOException($"CopyToAsyncWindowsNative: expected temp file missing: {tmpFi.FullName}");

                    tmpFi.MoveTo(target.Destination.FullName);
                }

                foreach (var target in targets)
                {
                    File.SetCreationTime(target.Destination.FullName, copyprogress.SourceFile.CreationTime);
                    File.SetLastWriteTime(target.Destination.FullName, copyprogress.SourceFile.LastWriteTime);
                    File.SetLastAccessTime(target.Destination.FullName, copyprogress.SourceFile.LastAccessTime);
                }

                if (copyprogress.MoveFile) copyprogress.SourceFile.Delete();

                completed = true;
                copyprogress.BytesCopied = copyprogress.FileSizeBytes;
                copyprogress.IsCompleted = true;
                copyprogress.Callback?.Invoke(copyprogress);
                return copyprogress;
            }
            catch (OperationCanceledException)
            {
                copyprogress.Cancelled = true;
                copyprogress.IsCompleted = false;
                copyprogress.Callback?.Invoke(copyprogress);
                throw;
            }
            finally
            {
                if (!completed)
                {
                    foreach (var target in targets)
                    {
                        SafeDeleteFile(target.TempPath);
                        SafeDeleteFile(target.StatePath);
                    }
                }
            }
        }

        public static bool IsImmutable(this FileInfo fi)
        {
            if (!fi.Exists) throw new FileNotFoundException($"File {fi.FullName} not found");
            var now = DateTime.Now;
            while (fi.LastAccessTime == now)
                now = now.AddMinutes(-5);
            try
            {
                File.SetLastAccessTime(fi.FullName, now);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        public static bool IsImmutable(this FileEntry fe)
        {
            var fi = new FileInfo(fe.FullName);
            return fi.IsImmutable();
        }

                // --------------------------------------------------------------------
        // IO helpers
        // --------------------------------------------------------------------

                        private static long ChunkOffset(int chunkIndex, long chunkSizeBytes)
            => checked(chunkIndex * chunkSizeBytes);

        private static long ChunkLength(int chunkIndex, long fileSizeBytes, long chunkSizeBytes)
        {
            var offset = ChunkOffset(chunkIndex, chunkSizeBytes);
            return Math.Max(0, Math.Min(chunkSizeBytes, fileSizeBytes - offset));
        }

        private static string ChunkedCopyStatePath(CopyTarget target)
            => target.TempPath + ".chunks.state";

        private static string? FindConfirmedChunkHash(ChunkedCopyState[] states, int chunkIndex)
        {
            string? hash = null;
            foreach (var state in states)
            {
                var candidate = state.ChunkHashes[chunkIndex];
                if (string.IsNullOrWhiteSpace(candidate)) return null;
                if (hash == null)
                    hash = candidate;
                else if (!string.Equals(hash, candidate, StringComparison.Ordinal))
                    return null;
            }

            return hash;
        }

        private static int FindContiguousDonePrefix(bool[] chunkDone)
        {
            var prefix = 0;
            while (prefix < chunkDone.Length && chunkDone[prefix])
                prefix++;
            return prefix;
        }

        private static int FindConfirmedSha1Prefix(ChunkedCopyState[] states, bool[] chunkDone, out byte[]? stateBlob)
        {
            stateBlob = null;
            if (states.Length == 0) return 0;

            var prefix = states.Min(s =>
                s.HashEnabled && s.HashAlgorithmId == 1 && s.Sha1StateBlob.Length > 0
                    ? s.Sha1PrefixChunkCount
                    : 0);

            prefix = Math.Min(prefix, FindContiguousDonePrefix(chunkDone));
            if (prefix <= 0) return 0;

            var chosen = states
                .Where(s => s.HashEnabled && s.HashAlgorithmId == 1 && s.Sha1PrefixChunkCount == prefix)
                .Select(s => s.Sha1StateBlob)
                .FirstOrDefault(b => b.Length > 0);

            if (chosen == null || !CanImportSha1State(chosen))
                return 0;

            stateBlob = chosen;
            return prefix;
        }

        private static bool CanImportSha1State(byte[] blob)
        {
            try
            {
                var sha = new Sha1Stateful();
                sha.ImportState(blob);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task WriteOrderedChunkToTargetsAsync(
            CopyTarget[] targets,
            long offset,
            byte[] buffer,
            int count,
            int bufferSize,
            bool verifyChunkWrites,
            byte[] expectedHash,
            ChunkedCopyDiagnostics diagnostics,
            CancellationToken token)
        {
            var streams = new FileStream[targets.Length];
            try
            {
                // Do not change these streams to async/overlapped I/O casually. The .NET Framework
                // async FileStream implementation was the bottleneck in our SMB tests; synchronous
                // writes issued from bounded worker tasks reached expected gigabit throughput.
                // .NET 8 async I/O was much better, but this file must behave well for both runtimes.
                for (int i = 0; i < targets.Length; i++)
                {
                    streams[i] = new FileStream(
                        targets[i].TempPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        FileRelayStreamBufferSize,
                        FileOptions.None);
                    streams[i].Seek(offset, SeekOrigin.Begin);
                }

                var written = 0;
                while (written < count)
                {
                    token.ThrowIfCancellationRequested();
                    var writeCount = Math.Min(bufferSize, count - written);
                    for (int targetIndex = 0; targetIndex < streams.Length; targetIndex++)
                        TimedWrite(streams[targetIndex], buffer, written, writeCount, token, diagnostics, targetIndex);

                    written += writeCount;
                }

                var flushTicks = Stopwatch.GetTimestamp();
                var flushes = streams.Select(s => s.FlushAsync(token)).ToArray();
                await Task.WhenAll(flushes).ConfigureAwait(false);
                diagnostics.AddFlush(Stopwatch.GetTimestamp() - flushTicks);

                foreach (var s in streams)
                {
                    try { s.Dispose(); } catch { }
                }

                if (verifyChunkWrites)
                {
                    foreach (var target in targets)
                    {
                        var destHash = await ComputeRangeXxHash3Async(target.TempPath, offset, count, bufferSize, token).ConfigureAwait(false);
                        if (!expectedHash.SequenceEqual(destHash))
                            throw new IOException($"Chunk verification failed for '{target.Destination.FullName}' at offset {offset}.");
                    }
                }
            }
            finally
            {
                foreach (var s in streams)
                {
                    try { s?.Dispose(); } catch { }
                }
            }
        }

        private static byte[] RentChunkBuffer(int minimumLength)
        {
            while (ChunkBufferPool.TryTake(out var buffer))
            {
                if (buffer.Length >= minimumLength)
                    return buffer;
            }

            return new byte[minimumLength];
        }

        private static void ReturnChunkBuffer(byte[] buffer)
        {
            if (buffer.Length > 0)
                ChunkBufferPool.Add(buffer);
        }

        private static void TimedWrite(FileStream stream, byte[] buffer, int offset, int count, CancellationToken token, ChunkedCopyDiagnostics diagnostics, int targetIndex)
        {
            var writeTicks = Stopwatch.GetTimestamp();
            token.ThrowIfCancellationRequested();
            stream.Write(buffer, offset, count);
            diagnostics.AddWrite(targetIndex, Stopwatch.GetTimestamp() - writeTicks);
        }

        private static async Task<byte[]> ComputeRangeXxHash3Async(string path, long offset, long length, int bufferSize, CancellationToken token)
        {
            var xxh = new System.IO.Hashing.XxHash3();
            var buffer = new byte[bufferSize];
            var remaining = length;
            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            source.Seek(offset, SeekOrigin.Begin);
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Unexpected end of file during chunk verification.");
                xxh.Append(new ReadOnlySpan<byte>(buffer, 0, read));
                remaining -= read;
            }

            return xxh.GetHashAndReset();
        }

        private static byte[] ComputeWholeFileSha1(string path, int bufferSize, CancellationToken token)
        {
            using var sha = SHA1.Create();
            var buffer = new byte[bufferSize];

            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha.Hash ?? Array.Empty<byte>();
        }

        private static void SafeDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort */ }
        }

        private static void ThrowDiskFullOrWrap(IOException ioEx, FileInfo destination, string action)
        {
            const int ERROR_DISK_FULL = 112;
            bool diskFull =
                ioEx.HResult == unchecked((int)0x80070070) ||
                (ioEx.InnerException is Win32Exception w32 && w32.NativeErrorCode == ERROR_DISK_FULL);

            if (diskFull)
            {
                throw new IOException(
                    $"Not enough disk space while {action} for destination '{destination.FullName}'.",
                    ioEx);
            }

            throw new IOException(
                $"I/O error while {action} for destination '{destination.FullName}': {ioEx.Message}",
                ioEx);
        }

        // --------------------------------------------------------------------
        // Per-destination mapping
        // --------------------------------------------------------------------

        private const int COPY_FILE_NO_BUFFERING = 0x00001000;
        private const int ERROR_REQUEST_ABORTED = 1235;

        private delegate CopyProgressResult CopyProgressRoutine(
            long totalFileSize,
            long totalBytesTransferred,
            long streamSize,
            long streamBytesTransferred,
            uint streamNumber,
            CopyProgressCallbackReason callbackReason,
            IntPtr sourceFile,
            IntPtr destinationFile,
            IntPtr data);

        private enum CopyProgressResult : uint
        {
            PROGRESS_CONTINUE = 0,
            PROGRESS_CANCEL = 1,
            PROGRESS_STOP = 2,
            PROGRESS_QUIET = 3
        }

        private enum CopyProgressCallbackReason : uint
        {
            CALLBACK_CHUNK_FINISHED = 0,
            CALLBACK_STREAM_SWITCH = 1
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CopyFileEx(
            string existingFileName,
            string newFileName,
            CopyProgressRoutine progressRoutine,
            IntPtr data,
            ref int cancel,
            int copyFlags);

        private sealed class ChunkedCopyState
        {
            public const int Magic = unchecked((int)0x4D44434B); // "MDDK"
            public const int Version = 2;

            public long SourceLength;
            public long SourceLastWriteUtcTicks;
            public long ChunkSizeBytes;
            public int TotalChunks;
            public string?[] ChunkHashes = Array.Empty<string?>();
            public bool HashEnabled;
            public int HashAlgorithmId;
            public int Sha1PrefixChunkCount;
            public byte[] Sha1StateBlob = Array.Empty<byte>();

            public static ChunkedCopyState Create(FileInfo source, long chunkSizeBytes, int totalChunks)
            {
                return new ChunkedCopyState
                {
                    SourceLength = source.Length,
                    SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                    ChunkSizeBytes = chunkSizeBytes,
                    TotalChunks = totalChunks,
                    ChunkHashes = new string?[totalChunks]
                };
            }

            public void SetSha1State(int prefixChunkCount, byte[] stateBlob)
            {
                HashEnabled = true;
                HashAlgorithmId = 1;
                Sha1PrefixChunkCount = prefixChunkCount;
                Sha1StateBlob = stateBlob;
            }
        }

        private sealed class ChunkedCopyDiagnostics
        {
            private long readTicks;
            private long hashTicks;
            private long flushTicks;
            private long peakHeapBytes;
            private readonly long[] writeTicksByTarget;

            public ChunkedCopyDiagnostics(int targetCount)
            {
                writeTicksByTarget = new long[targetCount];
            }

            public void AddRead(long ticks) => Interlocked.Add(ref readTicks, ticks);
            public void AddHash(long ticks) => Interlocked.Add(ref hashTicks, ticks);
            public void AddFlush(long ticks) => Interlocked.Add(ref flushTicks, ticks);
            public void AddWrite(int targetIndex, long ticks) => Interlocked.Add(ref writeTicksByTarget[targetIndex], ticks);

            public void SamplePeakHeap()
            {
                var current = GC.GetTotalMemory(false);
                long previous;
                do
                {
                    previous = Volatile.Read(ref peakHeapBytes);
                    if (current <= previous) return;
                } while (Interlocked.CompareExchange(ref peakHeapBytes, current, previous) != previous);
            }

            public long PeakHeapBytes => Volatile.Read(ref peakHeapBytes);

            public override string ToString()
            {
                static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
                var writes = string.Join(", ", writeTicksByTarget.Select((ticks, index) => $"w{index + 1}={Seconds(Volatile.Read(ref writeTicksByTarget[index])):N1}s"));
                var peakMiB = Volatile.Read(ref peakHeapBytes) / 1024 / 1024;
                return $"read={Seconds(Volatile.Read(ref readTicks)):N1}s; hash={Seconds(Volatile.Read(ref hashTicks)):N1}s; {writes}; flush={Seconds(Volatile.Read(ref flushTicks)):N1}s; peakHeap={peakMiB}MiB";
            }
        }

        private sealed class SequentialChunkWork
        {
            public SequentialChunkWork(int chunkIndex, long offset, int count, byte[] buffer, byte[] hash)
            {
                ChunkIndex = chunkIndex;
                Offset = offset;
                Count = count;
                Buffer = buffer;
                Hash = hash;
            }

            public int ChunkIndex { get; }
            public long Offset { get; }
            public int Count { get; }
            public byte[] Buffer { get; }
            public byte[] Hash { get; }
        }

        private sealed class HashWork
        {
            public HashWork(int chunkIndex, long offset, int count, byte[] buffer, bool kept)
            {
                ChunkIndex = chunkIndex;
                Offset = offset;
                Count = count;
                Buffer = buffer;
                Kept = kept;
            }

            public int ChunkIndex { get; }
            public long Offset { get; }
            public int Count { get; }
            public byte[] Buffer { get; }
            public bool Kept { get; }
        }

        private static class ChunkedCopyStateFile
        {
            public static ChunkedCopyState? TryRead(string statePath, FileInfo source, long chunkSizeBytes, int totalChunks)
            {
                if (!File.Exists(statePath)) return null;

                try
                {
                    using var fs = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var br = new BinaryReader(fs);

                    var magic = br.ReadInt32();
                    var version = br.ReadInt32();
                    if (magic != ChunkedCopyState.Magic) return null;
                    if (version != 1 && version != ChunkedCopyState.Version) return null;

                    var state = new ChunkedCopyState
                    {
                        SourceLength = br.ReadInt64(),
                        SourceLastWriteUtcTicks = br.ReadInt64(),
                        ChunkSizeBytes = br.ReadInt64(),
                        TotalChunks = br.ReadInt32()
                    };

                    if (state.SourceLength != source.Length) return null;
                    if (state.SourceLastWriteUtcTicks != source.LastWriteTimeUtc.Ticks) return null;
                    if (state.ChunkSizeBytes != chunkSizeBytes) return null;
                    if (state.TotalChunks != totalChunks) return null;
                    if (state.TotalChunks < 0 || state.TotalChunks > 10_000_000) return null;

                    state.ChunkHashes = new string?[state.TotalChunks];
                    for (int i = 0; i < state.TotalChunks; i++)
                    {
                        var hasHash = br.ReadBoolean();
                        if (!hasHash) continue;
                        var hash = br.ReadString();
                        if (string.IsNullOrWhiteSpace(hash)) return null;
                        state.ChunkHashes[i] = hash;
                    }

                    if (version >= 2)
                    {
                        state.HashEnabled = br.ReadBoolean();
                        state.HashAlgorithmId = br.ReadInt32();
                        state.Sha1PrefixChunkCount = br.ReadInt32();
                        var blobLength = br.ReadInt32();

                        if (state.Sha1PrefixChunkCount < 0 || state.Sha1PrefixChunkCount > state.TotalChunks) return null;
                        if (blobLength < 0 || blobLength > 4096) return null;
                        state.Sha1StateBlob = blobLength > 0 ? br.ReadBytes(blobLength) : Array.Empty<byte>();

                        if (state.HashEnabled)
                        {
                            if (state.HashAlgorithmId != 1) return null;
                            if (state.Sha1PrefixChunkCount <= 0) return null;
                            if (state.Sha1StateBlob.Length == 0) return null;
                            if (!CanImportSha1State(state.Sha1StateBlob)) return null;
                        }
                    }

                    return state;
                }
                catch
                {
                    return null;
                }
            }

            public static void WriteAtomic(string statePath, ChunkedCopyState state, bool fullFlush)
            {
                var newPath = statePath + ".new";

                using (var fs = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(ChunkedCopyState.Magic);
                    bw.Write(ChunkedCopyState.Version);
                    bw.Write(state.SourceLength);
                    bw.Write(state.SourceLastWriteUtcTicks);
                    bw.Write(state.ChunkSizeBytes);
                    bw.Write(state.TotalChunks);

                    for (int i = 0; i < state.TotalChunks; i++)
                    {
                        var hash = state.ChunkHashes[i];
                        bw.Write(!string.IsNullOrWhiteSpace(hash));
                        if (!string.IsNullOrWhiteSpace(hash))
                            bw.Write(hash);
                    }

                    bw.Write(state.HashEnabled);
                    bw.Write(state.HashAlgorithmId);
                    bw.Write(state.Sha1PrefixChunkCount);
                    bw.Write(state.Sha1StateBlob?.Length ?? 0);
                    if (state.Sha1StateBlob != null && state.Sha1StateBlob.Length > 0)
                        bw.Write(state.Sha1StateBlob);

                    bw.Flush();
                    if (fullFlush)
                    {
                        try { fs.Flush(true); } catch { /* platform/fs may not support */ }
                    }
                    else
                    {
                        fs.Flush();
                    }
                }

                if (File.Exists(statePath))
                {
                    try
                    {
                        File.Replace(newPath, statePath, destinationBackupFileName: null);
                        return;
                    }
                    catch
                    {
                        // fallback below
                    }
                }

                SafeDeleteFile(statePath);
                File.Move(newPath, statePath);
            }
        }

        private sealed class CopyTarget
        {
            public FileInfo Destination { get; }
            public string TempPath { get; }
            public string StatePath { get; }

            public CopyTarget(FileInfo destination)
            {
                Destination = destination ?? throw new ArgumentNullException(nameof(destination));
                TempPath = destination.FullName + ".tmp";
                StatePath = TempPath + ".state";
            }
        }

    }

    public class FileCopyProgress
    {
        public FileInfo? SourceFile { get; set; }
        public FileInfo[]? Destinations { get; set; }
        public Action<FileCopyProgress>? Callback { get; set; }



        private string? filename = null;
        public string? FileName 
        { 
            get
            {
                if (filename == null && SourceFile != null)
                    filename = SourceFile.Name;
                return filename;
            }
            set => filename = value;
        } //legacy - pulled automatically from SourceFile 
        public long FileSizeBytes { get; set; } //legacy - pulled automatically from SourceFile
        public string OperationDuring { get; set; } = "Copying";
        public string OperationComplete { get; set; } = "Copy";
        public string? DiagnosticInfo { get; set; }
        public string? DiagnosticSummary { get; set; }
        public byte[]? Hash { get; set; }


        public bool Overwrite { get; set; } = false;
        public CancellationToken Token { get; set; } = default;
        public bool MoveFile { get; set; } = false;
        public FileCopyHashMode HashMode { get; set; } = FileCopyHashMode.NoHash;
        public bool ComputeHash
        {
            get => HashMode != FileCopyHashMode.NoHash;
            set => HashMode = value ? FileCopyHashMode.FastNativeHashWithResumeReread : FileCopyHashMode.NoHash;
        }
        public bool Resumable { get; set; } = true;

        public int BufferSize { get; set; } = 1024 * 1024 * 4; // 4MB
        public double MaxUsage { get; set; } = 1;
        public int PipelineBufferCount { get; set; } = 8;
        public int DestinationWriterCount { get; set; } = 1;
        public long ChunkSizeBytes { get; set; } = 50L * 1024 * 1024;
        public int ParallelChunks { get; set; } = 4;
        public bool OrderedChunkWrites { get; set; } = false;
        public bool VerifyChunkWrites { get; set; } = false;
        public TimeSpan ChunkStateFlushInterval { get; set; } = TimeSpan.FromSeconds(5);
        public bool FullFlushChunkState { get; set; } = false;
        public bool PreallocateDestinationFiles { get; set; } = false;
        public bool FullFlushOnCompletion { get; set; } = true;

        public DateTime StartTime { get; private set; }
        private Stopwatch? stopwatch = null;
        public Stopwatch? Stopwatch
        {
            get => stopwatch;
            set
            {
                stopwatch = value;
                StartTime = DateTime.Now;
            }
        }
        public bool Queued { get; set; } = false;
        public bool Cancelled { get; set; } = false;
        public bool IsCompleted { get; set; } = false;
        public bool IncompleteButNotError { get; set; } = false;


        //integrated callback - added 2025-10-28 for AzureTransferCoordinator
        public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromSeconds(1);
        private TimeSpan lastreport = TimeSpan.Zero;
        public void UpdateAndMaybeCallback(long addbytes)
        {
            BytesCopied += addbytes;
            if (Callback != null && (lastreport == TimeSpan.Zero || (Stopwatch?.Elapsed - lastreport) > ProgressReportInterval))
            {
                lastreport = Stopwatch?.Elapsed ?? TimeSpan.Zero;
                Callback(this);
            }
        }
        public bool HasIntegratedCallback => Callback != null && ProgressReportInterval > TimeSpan.Zero;


        public long BytesStart { get; set; } = 0;
        private long _BytesCopied;
        public long BytesCopied
        {
            get { return _BytesCopied; }
            set
            {
                if (value != _BytesCopied && value >= FileSizeBytes && FileSizeBytes > 0 && Stopwatch != null && Stopwatch.IsRunning)
                {
                    //IsCompleted = true;
                    Stopwatch.Stop();
                }
                _BytesCopied = value;
            }
        }
        public decimal PercentComplete
        {
            get
            {
                if (FileSizeBytes == 0) return 0;
                return BytesCopied / Convert.ToDecimal(FileSizeBytes);
            }
        }
        public double RateMBPerSec
        {
            get
            {
                if (Stopwatch == null) return 0;
                return (BytesCopied - BytesStart) / 1024.0 / 1024.0 / Stopwatch.Elapsed.TotalSeconds;
            }
        }
        public TimeSpan EstimatedRemaining
        {
            get
            {
                return TimeSpan.FromSeconds(BytesCopied == 0 ? 0 : Stopwatch.Elapsed.TotalSeconds * FileSizeBytes / BytesCopied) - Stopwatch.Elapsed;
            }
        }
        public override string ToString()
        {
            if (Cancelled)
                return $"{OperationDuring} of {FileName} cancelled";
            if (Queued)
                return $"{OperationDuring} of {FileName} queued...";
            var p = PercentComplete;
            var summary = string.IsNullOrWhiteSpace(DiagnosticSummary) ? "" : $" {{{DiagnosticSummary}}}";
            var info = string.IsNullOrWhiteSpace(DiagnosticInfo) ? "" : $" [{DiagnosticInfo}]";
            if (p == 0)
                return $"{OperationDuring} {FileName} - {Foundation.SizeDisplay(FileSizeBytes)}{info}";
            else if (p < 1)
                return $"{OperationDuring} {FileName} - {p * 100:N1}% - {RateMBPerSec:N1}MB/s...";
            else if (IsCompleted)
                return $"{OperationComplete} of {FileName}{summary} complete in {Stopwatch?.Elapsed} - {RateMBPerSec:N1}MB/s";
            else
                return $"{OperationComplete} of {FileName}{summary} finishing -  {Stopwatch?.Elapsed} - {RateMBPerSec:N1}MB/s";

        }
    }
}
