using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MDDFoundation
{
    public sealed class NASFileLock : IDisposable
    {
        private readonly string _lockPath;
        private readonly FileStream _stream;
        private bool _disposed;

        private NASFileLock(string lockPath, FileStream stream)
        {
            _lockPath = lockPath;
            _stream = stream;
        }

        /// <summary>
        /// Acquire an exclusive lock using an atomic lock file.
        /// </summary>
        /// <param name="lockPath">Path to the lock file (e.g. inbox.txt.lock)</param>
        /// <param name="maxAttempts">Maximum acquire attempts</param>
        /// <param name="staleAfter">Treat lock as stale after this duration</param>
        public static NASFileLock Acquire(
            string lockPath,
            int maxAttempts = 10,
            TimeSpan? staleAfter = null)
        {
            staleAfter ??= TimeSpan.FromSeconds(10); // you said ~10s

            var pid = Process.GetCurrentProcess().Id;
            Exception? lastError = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Attempt atomic creation
                    var fs = new FileStream(
                        lockPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None);

                    // Write metadata (use "now" at acquisition time)
                    var now = DateTime.UtcNow;
                    var content =
                        $"PID={pid}\n" +
                        $"UTC={now:O}\n";

                    var bytes = Encoding.UTF8.GetBytes(content);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);

                    // Optional: ensure mtime reflects "now" for stale logic used by other side
                    TryTouch(lockPath);

                    return new NASFileLock(lockPath, fs);
                }
                catch (IOException ioEx)
                {
                    lastError = ioEx;

                    // Check for stale lock
                    if (IsStale(lockPath, staleAfter.Value))
                    {
                        // Best-effort steal: delete and immediately retry
                        if (Debugger.IsAttached)
                        {
                            Debugger.Log(0, "FileLock", $"[FileLock] Stale lock detected at '{lockPath}', stealing...\n");
                        }
                        TryDelete(lockPath);
                        continue;
                    }

                    if (attempt == maxAttempts)
                        break;

                    Backoff(attempt);
                }
            }

            throw new IOException($"Failed to acquire lock '{lockPath}' after {maxAttempts} attempts.", lastError);
        }

        private static bool IsStale(string lockPath, TimeSpan staleAfter)
        {
            try
            {
                //If it doesn't exist, it's not stale(it’s just free)
                if (!File.Exists(lockPath))
                    return false;

                // Use file mtime (works fine for SMB and for symlink-as-file cases)
                var lastWriteUtc = File.GetLastWriteTimeUtc(lockPath);
                var age = DateTime.UtcNow - lastWriteUtc;

                // Guard against weird timestamps (clock skew / 1601/1970 nonsense)
                if (lastWriteUtc.Year < 2000)
                    return true;

                return age >= staleAfter;
            }
            catch
            {
                // If we can't read it, treat as not stale to avoid destructive behavior
                return false;
            }
        }


        private static void TryDelete(string lockPath)
        {
            try
            {
                if (!File.Exists(lockPath))
                    return;

                try
                {
                    // Clear readonly if present
                    var attr = File.GetAttributes(lockPath);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(lockPath, attr & ~FileAttributes.ReadOnly);
                }
                catch { /* ignore */ }

                File.Delete(lockPath);
            }
            catch
            {
                // ignore: someone else may have deleted it or we lost a race
            }
        }
        private static void TryTouch(string lockPath)
        {
            try
            {
                var now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(lockPath, now);
                File.SetLastAccessTimeUtc(lockPath, now);
            }
            catch
            {
                // ignore
            }
        }
        private static readonly Random s_random = new Random();
        private static readonly object s_randomLock = new object();

        private static void Backoff(int attempt)
        {
            var baseDelayMs = 50;
            var delay = baseDelayMs * (1 << Math.Min(attempt - 1, 6));
            int jitter;
            lock (s_randomLock)
            {
                jitter = s_random.Next(0, 50);
            }
            Thread.Sleep(delay + jitter);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _stream.Dispose();
            }
            catch { /* ignore */ }

            TryDelete(_lockPath);
        }
    }
}
