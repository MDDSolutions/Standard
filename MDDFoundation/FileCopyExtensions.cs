// File: FileCopyExtensions.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public static class FileCopyExtensions
    {
        // --------------------------------------------------------------------
        // Public API
        // --------------------------------------------------------------------

        /// <summary>
        /// Single-destination overload (wraps multicast).
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
        /// Multicast resumable copy with resumable SHA1(file) hashing.
        ///
        /// Deterministic per-destination temp + state:
        ///   tmp   = destination.FullName + ".tmp"
        ///   state = destination.FullName + ".tmp.state"
        ///
        /// Resume model:
        /// - Temp files are ALWAYS preallocated to source length when resumable==true (to avoid relying on Length at all).
        /// - The ONLY progress signal is the sidecar state file.
        /// - Resume offset is the MIN committed length across destinations.
        /// - Hash resume uses the SHA1 state snapshot stored in the state file for that MIN committed length.
        /// - On resume, all destinations are normalized to that offset:
        ///     * seek tmp streams to resumeOffset
        ///     * overwrite every destination's state file to exactly resumeOffset + chosen SHA1 state
        ///
        /// Hashing:
        /// - If computehash is true, we compute EXACT SHA1(file).
        /// - If resuming, we restore SHA1 internal state and continue hashing from resumeOffset without rereading.
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
            int bufferSize = 1024 * 1024, // 1MB
            double maxUsage = 1)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (destinations == null) throw new ArgumentNullException(nameof(destinations));
            if (destinations.Length == 0) throw new ArgumentException("At least one destination is required.", nameof(destinations));
            if (!file.Exists) throw new IOException($"Source file {file.FullName} does not exist");
            if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            if (maxUsage < 0 || maxUsage > 1) throw new ArgumentOutOfRangeException(nameof(maxUsage));

            // Sanity: no null destinations, no dup full names
            if (destinations.Any(d => d == null)) throw new ArgumentException("Destinations contains a null entry.", nameof(destinations));
            var dup = destinations.GroupBy(d => d.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"Duplicate destination path: {dup.Key}", nameof(destinations));

            if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);

            long commitinterval = Convert.ToInt64(progressreportinterval.TotalMilliseconds * 60 * Stopwatch.Frequency / 1000);
            var lastcommit = Stopwatch.GetTimestamp();
            const long TargetBytes = 100L * 1024 * 1024; // 100 MiB
            long blocks = (TargetBytes + bufferSize - 1) / bufferSize; // ceiling division
            int maxcommitblocks = (blocks <= 0) ? 1 : (blocks > int.MaxValue ? int.MaxValue : (int)blocks);


            DutyCycleThrottle? throttle = null;
            if (maxUsage > 0 && maxUsage < 1)
            {
                var window = progressreportinterval;
                // You might want a minimum window like 250ms
                if (window < TimeSpan.FromMilliseconds(250)) window = TimeSpan.FromMilliseconds(250);

                throttle = new DutyCycleThrottle(maxUsage, window);
            }


            foreach (var d in destinations)
            {
                if (d.Directory == null || !d.Directory.Exists)
                    throw new IOException($"Destination folder {d.DirectoryName} does not exist or is not available");

                if (d.Exists && !overwrite)
                    throw new IOException($"Destination file {d.FullName} exists (and overwrite was not specified)");
            }


            long len = file.Length;

            FileCopyProgress? copyprogress = null;
            if (progresscallback != null)
            {
                copyprogress = new FileCopyProgress
                {
                    FileSizeBytes = len,
                    FileName = file.Name,
                    Stopwatch = Stopwatch.StartNew(),
                    Callback = progresscallback,
                    ProgressReportInterval = (progressreportinterval == default ? TimeSpan.FromSeconds(1) : progressreportinterval),
                };
            }

            byte[] finalHash = Array.Empty<byte>();
            bool toolatetocancel = false;

            var targets = destinations.Select(d => new CopyTarget(d)).ToArray();

            FileStream? source = null;
            FileStream[]? tempStreams = null;

            try
            {
                token.ThrowIfCancellationRequested();

                // Decide resume (min committed) and which SHA1 state to use (from a destination at min committed)
                long resumeOffset = 0;
                CopyResumeState? chosenStateAtResume = null;

                if (resumable)
                {
                    var resume = CopyResumeLogic.TryComputeMinCommittedResume(file, targets, bufferSize, computehash);

                    if (resume.CanResume)
                    {
                        resumeOffset = resume.ResumeOffset;
                        chosenStateAtResume = resume.StateAtResume; // includes SHA1 snapshot iff computehash
                        if (copyprogress != null)
                        {
                            copyprogress.BytesStart = resumeOffset;
                            copyprogress.BytesCopied = resumeOffset;
                            copyprogress.OperationDuring = "Resuming copy";
                            copyprogress.UpdateAndMaybeCallback(0);
                            copyprogress.OperationDuring = "Copying";   
                        }
                    }
                    else
                    {
                        // Restart from 0: clear old artifacts to avoid mixing generations
                        foreach (var t in targets)
                        {
                            SafeDeleteFile(t.TempPath);
                            SafeDeleteFile(t.StatePath);
                        }
                        resumeOffset = 0;
                        chosenStateAtResume = null;
                    }
                }
                else
                {
                    // Not resumable => clear old artifacts
                    foreach (var t in targets)
                    {
                        SafeDeleteFile(t.TempPath);
                        SafeDeleteFile(t.StatePath);
                    }
                }

                // Open source
                source = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: bufferSize,
                    useAsync: true);

                // Open tmp streams (exclusive)
                tempStreams = targets
                    .Select(t => new FileStream(
                        t.TempPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: bufferSize,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                    .ToArray();

                // ALWAYS preallocate tmp files when resumable (so length is never meaningful)
                if (resumable)
                {
                    foreach (var (ts, idx) in tempStreams.Select((s, i) => (s, i)))
                    {
                        try
                        {
                            ts.SetLength(len);
                        }
                        catch (IOException ioEx)
                        {
                            ThrowDiskFullOrWrap(ioEx, targets[idx].Destination, $"preallocating '{len}' bytes");
                        }
                    }
                }

                // Position streams
                source.Seek(resumeOffset, SeekOrigin.Begin);
                foreach (var ts in tempStreams)
                    ts.Seek(resumeOffset, SeekOrigin.Begin);

                // Hash setup (resumable SHA1)
                Sha1Stateful? sha1 = null;
                if (computehash)
                {
                    sha1 = new Sha1Stateful();

                    if (resumeOffset > 0)
                    {
                        if (chosenStateAtResume == null || !chosenStateAtResume.HashEnabled || chosenStateAtResume.Sha1StateBlob == null)
                            throw new IOException("Resumable hashing requested, but SHA1 state was missing at the chosen resume point.");

                        sha1.ImportState(chosenStateAtResume.Sha1StateBlob);
                    }
                    else
                    {
                        sha1.Reset();
                    }
                }

                // Normalize: overwrite all destination state files to match the chosen resumeOffset + chosen hash snapshot.
                // This makes future resumes deterministic even if some destinations were ahead.
                if (resumable)
                {
                    CopyResumeState normalized = CopyResumeState.Create(file, bufferSize, resumeOffset, sha1);
                    var normalizeWrites = targets.Select(t => CopyResumeStateFile.WriteAtomicAsync(t.StatePath, normalized, token)).ToArray();
                    await Task.WhenAll(normalizeWrites).ConfigureAwait(false);
                    chosenStateAtResume = normalized;
                }

                // Copy loop
                byte[] bufferA = new byte[bufferSize];
                byte[] bufferB = new byte[bufferSize];
                bool swap = false;

                Task[]? writers = null;
                int blocksSinceCommit = 0;

                var lastProgress = TimeSpan.Zero;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    byte[] buf = swap ? bufferA : bufferB;

                    throttle?.StartBusy();

                    int read = await source.ReadAsync(buf, 0, buf.Length, token).ConfigureAwait(false);
                    if (read == 0) break;

                    sha1?.Update(buf, 0, read);

                    await CompleteWrites(writers).ConfigureAwait(false);

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

                    copyprogress?.UpdateAndMaybeCallback(read);

                    blocksSinceCommit++;

                    if (resumable && (blocksSinceCommit >= maxcommitblocks || (Stopwatch.GetTimestamp() - lastcommit) >= commitinterval))
                    {
                        if (Debugger.IsAttached)
                            Debug.WriteLine($"{DateTime.Now:u}-CopyToAsync: committing at {MinPosition(tempStreams)} bytes after {blocksSinceCommit} blocks.");

                        await CompleteWrites(writers).ConfigureAwait(false);

                        await FlushAllCommittedAsync(tempStreams, token, false).ConfigureAwait(false);

                        long committed = MinPosition(tempStreams); // should be equal if writes+flush succeeded, but take min anyway.

                        var st = CopyResumeState.Create(file, bufferSize, committed, sha1);
                        var stateWrites = targets.Select(t => CopyResumeStateFile.WriteAtomicAsync(t.StatePath, st, token)).ToArray();
                        await Task.WhenAll(stateWrites).ConfigureAwait(false);

                        blocksSinceCommit = 0;
                        lastcommit = Stopwatch.GetTimestamp();
                        if (Debugger.IsAttached)
                            Debug.WriteLine($"{DateTime.Now:u}-CopyToAsync: commit complete.");
                    }
                    if (throttle != null)
                        await throttle.ThrottleIfNeededAsync(token).ConfigureAwait(false);
                }

                await CompleteWrites(writers).ConfigureAwait(false);

                await FlushAllCommittedAsync(tempStreams, token, true).ConfigureAwait(false);

                if (resumable)
                {
                    long committed = MinPosition(tempStreams);
                    var st = CopyResumeState.Create(file, bufferSize, committed, sha1);
                    var stateWrites = targets.Select(t => CopyResumeStateFile.WriteAtomicAsync(t.StatePath, st, token)).ToArray();
                    await Task.WhenAll(stateWrites).ConfigureAwait(false);
                }

                if (sha1 != null)
                {
                    finalHash = sha1.FinalizeHash();
                    if (copyprogress != null) copyprogress.Hash = finalHash;
                }

                token.ThrowIfCancellationRequested();
                toolatetocancel = true;

                // Dispose streams before rename
                foreach (var ts in tempStreams) ts.Dispose();
                tempStreams = null;
                source.Dispose();
                source = null;

                // Swap into place (delete destination if overwrite)
                foreach (var t in targets)
                {
                    if (t.Destination.Exists) t.Destination.Delete();
                }

                foreach (var t in targets)
                {
                    var tmpFi = new FileInfo(t.TempPath);
                    tmpFi.Refresh();
                    if (!tmpFi.Exists)
                        throw new IOException($"CopyToAsync: expected temp file missing: {tmpFi.FullName}");

                    tmpFi.MoveTo(t.Destination.FullName);
                }

                // Verify existence + set times + cleanup state
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

                    SafeDeleteFile(t.StatePath);
                }

                if (MoveFile) file.Delete();

                if (progresscallback != null && copyprogress != null)
                {
                    copyprogress.BytesCopied = len;
                    progresscallback(copyprogress);
                }

                return finalHash;
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
                try { source?.Dispose(); } catch { }

                if (tempStreams != null)
                {
                    foreach (var ts in tempStreams)
                    {
                        try { ts?.Dispose(); } catch { }
                    }
                }

                // If not resumable and not completed, cleanup tmp/state
                if (!toolatetocancel && !resumable)
                {
                    foreach (var t in targets)
                    {
                        SafeDeleteFile(t.TempPath);
                        SafeDeleteFile(t.StatePath);
                    }
                }
            }
        }

        private static async Task CompleteWrites(Task[]? writers)
        {
            if (writers != null)
            {
                var sw = Stopwatch.StartNew();
                await Task.WhenAll(writers).ConfigureAwait(false);
                sw.Stop();
                if (Debugger.IsAttached && sw.ElapsedMilliseconds > 200)
                    Debug.WriteLine($"{DateTime.Now:u}-CopyToAsync: all writes completed in {sw.ElapsedMilliseconds} ms.");
            }
        }

        // --------------------------------------------------------------------
        // IO helpers
        // --------------------------------------------------------------------

        private static async Task FlushAllCommittedAsync(FileStream[] streams, CancellationToken token, bool fullflush = false)
        {
            var sw = Stopwatch.StartNew();
            var flushes = streams.Select(s => s.FlushAsync(token)).ToArray();
            await Task.WhenAll(flushes).ConfigureAwait(false);

            if (fullflush)
            {
                foreach (var s in streams)
                {
                    try { s.Flush(true); } catch { /* platform/fs may not support */ }
                }
            }
            sw.Stop();
            if (Debugger.IsAttached && sw.ElapsedMilliseconds > 200)
                Debug.WriteLine($"{DateTime.Now:u}-CopyToAsync: all flushes completed in {sw.ElapsedMilliseconds} ms.");
        }

        private static long MinPosition(FileStream[] streams)
        {
            long committed = streams[0].Position;
            for (int i = 1; i < streams.Length; i++)
                committed = Math.Min(committed, streams[i].Position);
            return committed;
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

        // --------------------------------------------------------------------
        // Resume state file (binary; includes optional SHA1 internal state)
        // --------------------------------------------------------------------

        private sealed class CopyResumeState
        {
            public const int Magic = unchecked((int)0x4D444443); // "MDDC"
            public const int Version = 2; // hash-state support

            public long CommittedLength;
            public int BufferSize;
            public long SourceLength;
            public long SourceLastWriteUtcTicks;

            public bool HashEnabled;
            public int HashAlgorithmId;      // 1 = SHA1
            public byte[] Sha1StateBlob = Array.Empty<byte>();     // Sha1Stateful.ExportState()

            public static CopyResumeState Create(FileInfo source, int bufferSize, long committedLength, Sha1Stateful? sha1OrNull)
            {
                var st = new CopyResumeState
                {
                    CommittedLength = committedLength,
                    BufferSize = bufferSize,
                    SourceLength = source.Length,
                    SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks
                };

                if (sha1OrNull != null)
                {
                    st.HashEnabled = true;
                    st.HashAlgorithmId = 1;
                    st.Sha1StateBlob = sha1OrNull.ExportState();
                }
                return st;
            }
        }

        private static class CopyResumeStateFile
        {
            public static CopyResumeState? TryRead(string statePath)
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

                    var s = new CopyResumeState
                    {
                        CommittedLength = br.ReadInt64(),
                        BufferSize = br.ReadInt32(),
                        SourceLength = br.ReadInt64(),
                        SourceLastWriteUtcTicks = br.ReadInt64(),
                        HashEnabled = br.ReadBoolean(),
                        HashAlgorithmId = br.ReadInt32()
                    };

                    int blobLen = br.ReadInt32();
                    if (blobLen < 0 || blobLen > 4096) return null;
                    if (blobLen > 0)
                        s.Sha1StateBlob = br.ReadBytes(blobLen);

                    if (s.HashEnabled)
                    {
                        if (s.HashAlgorithmId != 1) return null;
                        if (s.Sha1StateBlob == null || s.Sha1StateBlob.Length == 0) return null;
                    }

                    return s;
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

                    bw.Write(state.HashEnabled);
                    bw.Write(state.HashAlgorithmId);

                    if (state.Sha1StateBlob != null && state.Sha1StateBlob.Length > 0)
                    {
                        bw.Write(state.Sha1StateBlob.Length);
                        bw.Write(state.Sha1StateBlob);
                    }
                    else
                    {
                        bw.Write(0);
                    }

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
            internal readonly struct MinResumeDecision
            {
                public bool CanResume { get; }
                public long ResumeOffset { get; }
                public CopyResumeState? StateAtResume { get; }

                public MinResumeDecision(bool canResume, long resumeOffset, CopyResumeState? stateAtResume)
                {
                    CanResume = canResume;
                    ResumeOffset = resumeOffset;
                    StateAtResume = stateAtResume;
                }
            }

            /// <summary>
            /// Choose resumeOffset = min(committedLength) across destinations.
            /// Choose SHA1 state snapshot from a destination whose committedLength == that min.
            /// Does NOT require destinations to have identical committed lengths; we normalize after selection.
            /// </summary>
            public static MinResumeDecision TryComputeMinCommittedResume(FileInfo source, CopyTarget[] targets, int bufferSize, bool computehash)
            {
                CopyResumeState[] states = new CopyResumeState[targets.Length];

                long? minCommitted = null;

                for (int i = 0; i < targets.Length; i++)
                {
                    var t = targets[i];

                    if (!File.Exists(t.StatePath))
                        return new MinResumeDecision(false, 0, null);

                    var st = CopyResumeStateFile.TryRead(t.StatePath);
                    if (st == null)
                        return new MinResumeDecision(false, 0, null);

                    // Validate source identity + parameters
                    //if (st.BufferSize != bufferSize) return new MinResumeDecision(false, 0, null);
                    // BufferSize does NOT affect resume correctness; it only affects throughput/behavior.
                    // We keep it for observability but do not require it to match.

                    if (st.SourceLength != source.Length) return new MinResumeDecision(false, 0, null);
                    if (st.SourceLastWriteUtcTicks != source.LastWriteTimeUtc.Ticks) return new MinResumeDecision(false, 0, null);

                    // Validate hash requirements
                    if (computehash)
                    {
                        if (!st.HashEnabled) return new MinResumeDecision(false, 0, null);
                        if (st.HashAlgorithmId != 1) return new MinResumeDecision(false, 0, null);
                        if (st.Sha1StateBlob == null || st.Sha1StateBlob.Length == 0) return new MinResumeDecision(false, 0, null);
                    }

                    if (st.CommittedLength < 0) return new MinResumeDecision(false, 0, null);

                    states[i] = st;

                    minCommitted = minCommitted.HasValue ? Math.Min(minCommitted.Value, st.CommittedLength) : st.CommittedLength;
                }

                if (!minCommitted.HasValue || minCommitted.Value <= 0)
                    return new MinResumeDecision(false, 0, null);

                long min = minCommitted.Value;

                // Find a state at exactly min committed length
                // (There should always be at least one, by definition of min.)
                CopyResumeState? chosen = null;
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].CommittedLength == min)
                    {
                        chosen = states[i];
                        break;
                    }
                }

                if (chosen == null)
                    return new MinResumeDecision(false, 0, null);

                // If hashing is required, ensure chosen has SHA1 state (already validated).
                // Also sanity check state blob is structurally valid by attempting import into a throwaway SHA1.
                if (computehash)
                {
                    try
                    {
                        var tmp = new Sha1Stateful();
                        tmp.ImportState(chosen.Sha1StateBlob);
                    }
                    catch
                    {
                        return new MinResumeDecision(false, 0, null);
                    }
                }

                return new MinResumeDecision(true, min, chosen);
            }
        }
    }
}
