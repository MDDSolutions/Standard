// File: FileCopyExtensions.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public static class FileCopyExtensions
    {
        /// <summary>
        /// Single-destination overload: forwards to multicast version.
        /// </summary>
        public static Task<byte[]> CopyToAsync(
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
            int commitEveryNBlocks = 8,
            bool preallocateTemps = false)
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
                commitEveryNBlocks,
                preallocateTemps);
        }

        /// <summary>
        /// Multicast resumable copy.
        ///
        /// Deterministic per-destination temp + state:
        ///   tmp   = destination.FullName + ".tmp"
        ///   state = destination.FullName + ".tmp.state"
        ///
        /// Resume safety:
        /// - We only consider bytes "committed" if recorded in the state file.
        /// - State is updated only after writes are awaited and all temp streams are flushed.
        /// - Resume is all-or-nothing across destinations: if any destination can't resume safely,
        ///   we restart from 0 for all destinations to keep them in lockstep.
        ///
        /// Notes:
        /// - If computehash=true and resuming, we re-hash the prefix (0..resumeOffset) for correctness.
        /// - preallocateTemps=false by default (recommended). If true, we still rely on state, not Length, for resume.
        /// </summary>
        public static async Task<byte[]> CopyToAsync(
            this FileInfo file,
            FileInfo[] destinations,
            bool overwrite,
            CancellationToken token,
            bool MoveFile = false,
            Action<FileCopyProgress>? progresscallback = null,
            TimeSpan progressreportinterval = default,
            bool computehash = false,
            bool resumable = true,
            int bufferSize = 1024 * 1024,      // 1MB
            int commitEveryNBlocks = 8,         // durability cadence
            bool preallocateTemps = false)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (destinations == null) throw new ArgumentNullException(nameof(destinations));
            if (destinations.Length == 0) throw new ArgumentException("At least one destination is required.", nameof(destinations));
            if (!file.Exists) throw new IOException($"Source file {file.FullName} does not exist");
            if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            if (commitEveryNBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(commitEveryNBlocks));

            // Ensure destination list is sane (no nulls, no dup full names)
            if (destinations.Any(d => d == null)) throw new ArgumentException("Destinations contains a null entry.", nameof(destinations));
            var dup = destinations.GroupBy(d => d.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"Duplicate destination path: {dup.Key}", nameof(destinations));

            foreach (var d in destinations)
            {
                if (d.Directory == null || !d.Directory.Exists)
                    throw new IOException($"Destination folder {d.DirectoryName} does not exist or is not available");

                if (d.Exists && !overwrite)
                    throw new IOException($"Destination file {d.FullName} exists (and overwrite was not specified)");
            }

            if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);

            long len = file.Length;
            FileCopyProgress? copyprogress = null;

            if (progresscallback != null)
            {
                copyprogress = new FileCopyProgress
                {
                    FileSizeBytes = len,
                    FileName = file.Name,
                    Stopwatch = Stopwatch.StartNew()
                };
            }

            byte[] finalhash = Array.Empty<byte>();
            bool toolatetocancel = false;

            // Build per-destination temp/state mapping
            var targets = destinations
                .Select(d => new CopyTarget(d))
                .ToArray();

            FileStream? source = null;
            FileStream[]? tempStreams = null;

            try
            {
                token.ThrowIfCancellationRequested();

                // Decide resume offset (lockstep across all destinations)
                long resumeOffset = 0;

                if (resumable)
                {
                    var resumeInfo = CopyResumeLogic.TryComputeLockstepResumeOffset(file, targets, bufferSize);

                    if (resumeInfo.CanResume)
                    {
                        resumeOffset = resumeInfo.ResumeOffset;
                    }
                    else
                    {
                        // Restart from 0 for all (delete any partial tmp/state so we don't mix generations)
                        foreach (var t in targets)
                        {
                            SafeDeleteFile(t.TempPath);
                            SafeDeleteFile(t.StatePath);
                        }
                        resumeOffset = 0;
                    }
                }
                else
                {
                    // Not resumable => clear any old artifacts
                    foreach (var t in targets)
                    {
                        SafeDeleteFile(t.TempPath);
                        SafeDeleteFile(t.StatePath);
                    }
                }

                if (copyprogress != null)
                    copyprogress.BytesCopied = resumeOffset;

                // Open source
                source = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: bufferSize,
                    useAsync: true);

                // Open tmp streams
                tempStreams = targets
                    .Select(t => new FileStream(
                        t.TempPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: bufferSize,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                    .ToArray();

                // Position / truncate temps
                if (resumeOffset > 0)
                {
                    source.Seek(resumeOffset, SeekOrigin.Begin);
                    foreach (var ts in tempStreams)
                        ts.Seek(resumeOffset, SeekOrigin.Begin);
                }
                else
                {
                    // Fresh: truncate and create initial states
                    foreach (var ts in tempStreams)
                        ts.SetLength(0);

                    foreach (var t in targets)
                    {
                        var st = CopyResumeState.CreateInitial(file, bufferSize);
                        await CopyResumeStateFile.WriteAtomicAsync(t.StatePath, st, token).ConfigureAwait(false);
                    }
                }

                // Optional preallocation (not recommended for resumable, but supported)
                if (preallocateTemps)
                {
                    foreach (var (ts, idx) in tempStreams.Select((s, i) => (s, i)))
                    {
                        try
                        {
                            ts.SetLength(len);
                            ts.Seek(resumeOffset, SeekOrigin.Begin);
                        }
                        catch (IOException ioEx)
                        {
                            ThrowDiskFullOrWrap(ioEx, targets[idx].Destination, $"preallocating '{len}' bytes");
                        }
                    }
                }

                // Hashing
                using SHA1? hash = computehash ? SHA1.Create() : null;
                if (hash != null)
                {
                    hash.Initialize();

                    if (resumeOffset > 0)
                    {
                        // Hash prefix (0..resumeOffset)
                        source.Seek(0, SeekOrigin.Begin);
                        byte[] prefixBuf = new byte[bufferSize];
                        long remaining = resumeOffset;

                        while (remaining > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            int want = (int)Math.Min(bufferSize, remaining);
                            int r = await source.ReadAsync(prefixBuf, 0, want, token).ConfigureAwait(false);
                            if (r == 0) throw new EndOfStreamException("Unexpected EOF while hashing resume prefix.");
                            hash.TransformBlock(prefixBuf, 0, r, null, 0);
                            remaining -= r;
                        }

                        source.Seek(resumeOffset, SeekOrigin.Begin);
                    }
                }

                // Copy loop (double-buffer read, multicast write)
                byte[] bufferA = new byte[bufferSize];
                byte[] bufferB = new byte[bufferSize];
                bool swap = false;

                Task[] writers = null;
                int blocksSinceCommit = 0;

                var last = TimeSpan.Zero;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    if (copyprogress != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                    {
                        last = copyprogress.Stopwatch.Elapsed;
                        progresscallback?.Invoke(copyprogress);
                    }

                    byte[] buf = swap ? bufferA : bufferB;
                    int read = await source.ReadAsync(buf, 0, buf.Length, token).ConfigureAwait(false);
                    if (read == 0) break;

                    if (hash != null)
                        hash.TransformBlock(buf, 0, read, null, 0);

                    if (writers != null)
                        await Task.WhenAll(writers).ConfigureAwait(false);

                    // Write to all destinations (preserve index so we can wrap errors with destination info)
                    writers = new Task[tempStreams.Length];
                    for (int i = 0; i < tempStreams.Length; i++)
                    {
                        var stream = tempStreams[i];
                        var destInfo = targets[i].Destination;

                        try
                        {
                            writers[i] = stream.WriteAsync(buf, 0, read, token);
                        }
                        catch (IOException ioEx)
                        {
                            ThrowDiskFullOrWrap(ioEx, destInfo, $"writing to '{destInfo.DirectoryName}'");
                            throw; // unreachable
                        }
                    }

                    swap = !swap;

                    if (copyprogress != null) copyprogress.BytesCopied += read;

                    blocksSinceCommit++;

                    // Commit cadence: await all writers, flush all tmp streams, then update all state files atomically
                    if (resumable && blocksSinceCommit >= commitEveryNBlocks)
                    {
                        await Task.WhenAll(writers).ConfigureAwait(false);

                        await FlushAllCommittedAsync(tempStreams, token).ConfigureAwait(false);

                        long committed = tempStreams[0].Position;
                        // sanity: they should all match; if not, commit the minimum
                        for (int i = 1; i < tempStreams.Length; i++)
                            committed = Math.Min(committed, tempStreams[i].Position);

                        var st = CopyResumeState.Create(file, bufferSize, committed);
                        var stateWrites = targets
                            .Select(t => CopyResumeStateFile.WriteAtomicAsync(t.StatePath, st, token))
                            .ToArray();

                        await Task.WhenAll(stateWrites).ConfigureAwait(false);

                        blocksSinceCommit = 0;
                    }
                }

                if (writers != null)
                    await Task.WhenAll(writers).ConfigureAwait(false);

                // Final flush + final state commit
                await FlushAllCommittedAsync(tempStreams, token).ConfigureAwait(false);

                if (resumable)
                {
                    long committed = tempStreams[0].Position;
                    for (int i = 1; i < tempStreams.Length; i++)
                        committed = Math.Min(committed, tempStreams[i].Position);

                    var st = CopyResumeState.Create(file, bufferSize, committed);
                    var stateWrites = targets
                        .Select(t => CopyResumeStateFile.WriteAtomicAsync(t.StatePath, st, token))
                        .ToArray();

                    await Task.WhenAll(stateWrites).ConfigureAwait(false);
                }

                // Finalize hash
                if (hash != null)
                {
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    finalhash = hash.Hash ?? Array.Empty<byte>();
                    if (copyprogress != null) copyprogress.Hash = finalhash;
                }

                token.ThrowIfCancellationRequested();
                toolatetocancel = true;

                // Dispose streams before renames
                foreach (var ts in tempStreams) ts.Dispose();
                tempStreams = null;
                source.Dispose();
                source = null;

                // Swap into place (delete existing if overwrite)
                foreach (var t in targets)
                {
                    if (t.Destination.Exists) t.Destination.Delete();
                }

                foreach (var t in targets)
                {
                    // Move tmp => destination
                    var tmpFi = new FileInfo(t.TempPath);
                    tmpFi.Refresh();
                    if (!tmpFi.Exists)
                        throw new IOException($"CopyToAsync: expected temp file missing: {tmpFi.FullName}");

                    tmpFi.MoveTo(t.Destination.FullName);
                }

                // Ensure destination exists + set times (do not fire-and-forget)
                foreach (var t in targets)
                {
                    var waitloop = 0;
                    while (!t.Destination.Exists)
                    {
                        waitloop++;
                        await Task.Delay(100, token).ConfigureAwait(false);
                        t.Destination.Refresh();
                        if (waitloop > 10)
                            throw new Exception($"CopyToAsync: file {file.Name} - copy completed, but it did not appear in the destination '{t.Destination.FullName}'");
                    }

                    File.SetCreationTime(t.Destination.FullName, file.CreationTime);
                    File.SetLastWriteTime(t.Destination.FullName, file.LastWriteTime);
                    File.SetLastAccessTime(t.Destination.FullName, file.LastAccessTime);

                    // Clean up resume state after success
                    SafeDeleteFile(t.StatePath);
                }

                if (MoveFile) file.Delete();

                // Final progress
                if (progresscallback != null && copyprogress != null)
                {
                    copyprogress.BytesCopied = len;
                    progresscallback(copyprogress);
                }

                return finalhash;
            }
            catch (OperationCanceledException)
            {
                if (progresscallback != null && copyprogress != null)
                {
                    copyprogress.Cancelled = true;
                    progresscallback(copyprogress);
                }

                // Keep tmp+state for resume
                throw;
            }
            finally
            {
                // Ensure streams are disposed
                try { source?.Dispose(); } catch { }
                if (tempStreams != null)
                {
                    foreach (var ts in tempStreams)
                    {
                        try { ts?.Dispose(); } catch { }
                    }
                }

                // If we didn't finish swap, keep tmp+state for resumable, otherwise best-effort cleanup
                if (!toolatetocancel)
                {
                    // Do NOT delete tmp/state if resumable: we want resume to work after failures/cancel.
                    // If resumable is false, clean up.
                    if (!resumable)
                    {
                        foreach (var t in targets)
                        {
                            SafeDeleteFile(t.TempPath);
                            SafeDeleteFile(t.StatePath);
                        }
                    }
                }

                if (progresscallback != null && copyprogress != null && !toolatetocancel && token.IsCancellationRequested)
                {
                    copyprogress.Cancelled = true;
                    progresscallback(copyprogress);
                }
            }
        }

        // -------------------------
        // Helpers / error wrapping
        // -------------------------

        private static async Task FlushAllCommittedAsync(FileStream[] streams, CancellationToken token)
        {
            // FlushAsync first
            var flushes = streams.Select(s => s.FlushAsync(token)).ToArray();
            await Task.WhenAll(flushes).ConfigureAwait(false);

            // Then attempt Flush(true) for better durability (where supported)
            foreach (var s in streams)
            {
                try { s.Flush(true); } catch { /* platform/fs may not support */ }
            }
        }

        private static void SafeDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* best-effort */ }
        }

        private static void ThrowDiskFullOrWrap(IOException ioEx, FileInfo destination, string action)
        {
            // ERROR_DISK_FULL = 112, HResult = 0x80070070
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

            // Wrap with destination context but preserve original exception as inner
            throw new IOException(
                $"I/O error while {action} for destination '{destination.FullName}': {ioEx.Message}",
                ioEx);
        }

        // -------------------------
        // Per-destination target
        // -------------------------

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

        // ----------------------------
        // Resume state (binary, no JSON)
        // ----------------------------

        private sealed class CopyResumeState
        {
            public const int Magic = unchecked((int)0x4D444443); // "MDDC"
            public const int Version = 1;

            public long CommittedLength;
            public int BufferSize;
            public long SourceLength;
            public long SourceLastWriteUtcTicks;

            public static CopyResumeState CreateInitial(FileInfo source, int bufferSize)
            {
                return new CopyResumeState
                {
                    CommittedLength = 0,
                    BufferSize = bufferSize,
                    SourceLength = source.Length,
                    SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks
                };
            }

            public static CopyResumeState Create(FileInfo source, int bufferSize, long committedLength)
            {
                return new CopyResumeState
                {
                    CommittedLength = committedLength,
                    BufferSize = bufferSize,
                    SourceLength = source.Length,
                    SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks
                };
            }
        }

        private static class CopyResumeStateFile
        {
            public static CopyResumeState TryRead(string statePath)
            {
                if (!File.Exists(statePath)) return null;

                try
                {
                    using var fs = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var br = new BinaryReader(fs);

                    int magic = br.ReadInt32();
                    int ver = br.ReadInt32();

                    if (magic != CopyResumeState.Magic) return null;
                    if (ver != CopyResumeState.Version) return null;

                    return new CopyResumeState
                    {
                        CommittedLength = br.ReadInt64(),
                        BufferSize = br.ReadInt32(),
                        SourceLength = br.ReadInt64(),
                        SourceLastWriteUtcTicks = br.ReadInt64()
                    };
                }
                catch
                {
                    return null;
                }
            }

            public static async Task WriteAtomicAsync(string statePath, CopyResumeState state, CancellationToken token)
            {
                string newPath = statePath + ".new";

                using (var fs = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(CopyResumeState.Magic);
                    bw.Write(CopyResumeState.Version);

                    bw.Write(state.CommittedLength);
                    bw.Write(state.BufferSize);
                    bw.Write(state.SourceLength);
                    bw.Write(state.SourceLastWriteUtcTicks);

                    bw.Flush();
                    await fs.FlushAsync(token).ConfigureAwait(false);
                    try { fs.Flush(true); } catch { /* ignore */ }
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

        private static class CopyResumeLogic
        {
            internal readonly struct LockstepResumeDecision
            {
                public bool CanResume { get; }
                public long ResumeOffset { get; }

                public LockstepResumeDecision(bool canResume, long resumeOffset)
                {
                    CanResume = canResume;
                    ResumeOffset = resumeOffset;
                }
            }

            /// <summary>
            /// Computes a single resume offset for all destinations, or decides that we must restart from 0.
            /// All-or-nothing: if any destination can't resume safely, we return CanResume=false.
            /// </summary>
            public static LockstepResumeDecision TryComputeLockstepResumeOffset(FileInfo source, CopyTarget[] targets, int bufferSize)
            {
                long? minCommitted = null;

                foreach (var t in targets)
                {
                    var tmpFi = new FileInfo(t.TempPath);
                    if (!tmpFi.Exists) return new LockstepResumeDecision(false, 0);
                    if (!File.Exists(t.StatePath)) return new LockstepResumeDecision(false, 0);

                    var st = CopyResumeStateFile.TryRead(t.StatePath);
                    if (st == null) return new LockstepResumeDecision(false, 0);

                    if (st.BufferSize != bufferSize) return new LockstepResumeDecision(false, 0);
                    if (st.SourceLength != source.Length) return new LockstepResumeDecision(false, 0);
                    if (st.SourceLastWriteUtcTicks != source.LastWriteTimeUtc.Ticks) return new LockstepResumeDecision(false, 0);

                    tmpFi.Refresh();
                    if (tmpFi.Length <= 0 && st.CommittedLength <= 0) return new LockstepResumeDecision(false, 0);

                    // Clamp committed to tmp length (safety)
                    long committed = st.CommittedLength;
                    if (committed < 0) return new LockstepResumeDecision(false, 0);
                    committed = Math.Min(committed, tmpFi.Length);

                    minCommitted = minCommitted.HasValue ? Math.Min(minCommitted.Value, committed) : committed;
                }

                // Resume only if we have a meaningful offset (>0). If 0, treat as "restart".
                if (!minCommitted.HasValue || minCommitted.Value <= 0)
                    return new LockstepResumeDecision(false, 0);

                return new LockstepResumeDecision(true, minCommitted.Value);
            }
        }
    }
}
