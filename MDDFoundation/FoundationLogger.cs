using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public enum FoundationLogLevel
    {
        Trace,
        Debug,
        Info,
        Warning,
        Error,
        Critical
    }

    public sealed class FoundationLogger
    {
        public sealed class FoundationLogOptions
        {
            public long MaxFileSizeBytes { get; set; }
            public int MaxRotatedFiles { get; set; }
            public TimeSpan? MaxRotatedFileAge { get; set; }
            public bool PruneOnRotate { get; set; } = true;

            public FoundationLogOptions Clone()
            {
                return new FoundationLogOptions
                {
                    MaxFileSizeBytes = MaxFileSizeBytes,
                    MaxRotatedFiles = MaxRotatedFiles,
                    MaxRotatedFileAge = MaxRotatedFileAge,
                    PruneOnRotate = PruneOnRotate
                };
            }
        }

        private sealed class LogFileWorker
        {
            private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly object _stateLock = new object();
            private readonly object _fileLock = new object();
            private int _isRunning;

            public LogFileWorker(string fullFileName, FoundationLogOptions options)
            {
                FullFileName = fullFileName;
                Options = options;
            }

            public string FullFileName { get; }
            public Exception LastError { get; private set; }
            public FoundationLogOptions Options { get; set; }

            public void Enqueue(string entry, bool initialize)
            {
                if (initialize)
                {
                    ClearPending();
                    lock (_fileLock)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(FullFileName) ?? ".");
                            if (File.Exists(FullFileName))
                            {
                                File.Delete(FullFileName);
                            }
                        }
                        catch (Exception ex)
                        {
                            LastError = ex;
                            Debug.WriteLine(ex);
                        }
                    }
                }

                _queue.Enqueue(entry);
                EnsureWorkerRunning();
                _signal.Set();
            }

            public void Flush()
            {
                FlushPending();
            }

            public void Rotate()
            {
                FlushPending();
                lock (_fileLock)
                {
                    var fi = new FileInfo(FullFileName);
                    try
                    {
                        RotateLocked(fi);
                    }
                    catch (Exception ex)
                    {
                        LastError = ex;
                        Debug.WriteLine(ex);
                        throw;
                    }
                }
            }

            private void EnsureWorkerRunning()
            {
                lock (_stateLock)
                {
                    if (_isRunning != 0)
                    {
                        return;
                    }

                    _isRunning = 1;
                    Task.Run(WorkerLoop);
                }
            }

            private void WorkerLoop()
            {
                try
                {
                    while (true)
                    {
                        _signal.WaitOne(5000);
                        FlushPending();

                        lock (_stateLock)
                        {
                            if (_queue.IsEmpty)
                            {
                                _isRunning = 0;
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    Debug.WriteLine(ex);
                    lock (_stateLock)
                    {
                        _isRunning = 0;
                    }
                }
            }

            private void FlushPending()
            {
                var sb = new StringBuilder();
                while (_queue.TryDequeue(out var entry))
                {
                    sb.AppendLine(entry);
                }

                if (sb.Length == 0)
                {
                    return;
                }

                lock (_fileLock)
                {
                    try
                    {
                        var pendingText = sb.ToString();
                        Directory.CreateDirectory(Path.GetDirectoryName(FullFileName) ?? ".");
                        RotateIfNeeded(Encoding.UTF8.GetByteCount(pendingText));
                        File.AppendAllText(FullFileName, pendingText);
                    }
                    catch (Exception ex)
                    {
                        LastError = ex;
                        Debug.WriteLine(ex);
                    }
                }
            }

            private void RotateIfNeeded(int pendingBytes)
            {
                if (Options.MaxFileSizeBytes <= 0)
                {
                    return;
                }

                var fi = new FileInfo(FullFileName);
                if (!fi.Exists)
                {
                    return;
                }

                var projectedSize = fi.Length + pendingBytes;
                if (projectedSize >= Options.MaxFileSizeBytes)
                {
                    RotateLocked(fi);
                }
            }

            private void RotateLocked(FileInfo fi)
            {
                if (!fi.Exists)
                {
                    return;
                }

                var rotatedName = GetNextRotatedFileName(fi);
                fi.MoveTo(rotatedName);

                if (Options.PruneOnRotate)
                {
                    PruneRotatedFiles(fi);
                }
            }

            private static string GetNextRotatedFileName(FileInfo fi)
            {
                var extension = fi.Extension;
                var nameWithoutExtension = fi.Name.Substring(0, fi.Name.Length - extension.Length);
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var newName = Path.Combine(fi.DirectoryName ?? ".",
                    $"{nameWithoutExtension}_{timestamp}{extension}");

                var counter = 1;
                while (File.Exists(newName))
                {
                    newName = Path.Combine(fi.DirectoryName ?? ".",
                        $"{nameWithoutExtension}_{timestamp}_{counter}{extension}");
                    counter++;
                }

                return newName;
            }

            private void PruneRotatedFiles(FileInfo activeFile)
            {
                var files = GetRotatedFiles(activeFile).ToList();

                if (Options.MaxRotatedFileAge.HasValue)
                {
                    var cutoff = DateTime.Now.Subtract(Options.MaxRotatedFileAge.Value);
                    foreach (var file in files.Where(f => f.LastWriteTime < cutoff))
                    {
                        TryDelete(file);
                    }

                    files = GetRotatedFiles(activeFile).ToList();
                }

                if (Options.MaxRotatedFiles > 0)
                {
                    foreach (var file in files
                        .OrderByDescending(f => f.LastWriteTime)
                        .Skip(Options.MaxRotatedFiles))
                    {
                        TryDelete(file);
                    }
                }
            }

            private static IEnumerable<FileInfo> GetRotatedFiles(FileInfo activeFile)
            {
                var directory = activeFile.Directory;
                if (directory == null || !directory.Exists)
                {
                    return Enumerable.Empty<FileInfo>();
                }

                var extension = activeFile.Extension;
                var nameWithoutExtension = activeFile.Name.Substring(0, activeFile.Name.Length - extension.Length);
                var prefix = nameWithoutExtension + "_";

                return directory.GetFiles(prefix + "*" + extension)
                    .Where(f => !string.Equals(f.FullName, activeFile.FullName, StringComparison.OrdinalIgnoreCase));
            }

            private void TryDelete(FileInfo file)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    Debug.WriteLine(ex);
                }
            }

            private void ClearPending()
            {
                while (_queue.TryDequeue(out _))
                {
                }
            }
        }

        private readonly ConcurrentDictionary<string, LogFileWorker> _workers =
            new ConcurrentDictionary<string, LogFileWorker>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, FoundationLogOptions> _options =
            new ConcurrentDictionary<string, FoundationLogOptions>(StringComparer.OrdinalIgnoreCase);

        public static FoundationLogger Default { get; } = new FoundationLogger();

        public Exception LastError { get; private set; }

        public FoundationLogOptions DefaultOptions { get; } = new FoundationLogOptions();

        public void Configure(FoundationLogOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            CopyOptions(options, DefaultOptions);

            foreach (var worker in _workers)
            {
                if (!_options.ContainsKey(worker.Key))
                {
                    worker.Value.Options = DefaultOptions;
                }
            }
        }

        public void Configure(string fileName, FoundationLogOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var fullFileName = ResolveLogFileName(fileName);
            var clonedOptions = options.Clone();
            _options[fullFileName] = clonedOptions;
            if (_workers.TryGetValue(fullFileName, out var worker))
            {
                worker.Options = clonedOptions;
            }
        }

        public string ResolveLogFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = Foundation.DefaultLogFileName;
            }

            return FoundationAppPaths.ResolveLogFile(fileName);
        }

        public void Write(string message, FoundationLogLevel level = FoundationLogLevel.Info, string source = null,
            bool initialize = false, string fileName = null)
        {
            var fullFileName = ResolveLogFileName(fileName);
            var worker = _workers.GetOrAdd(fullFileName, name => new LogFileWorker(name, GetOptionsForFile(name)));
            worker.Enqueue(FormatEntry(message, level, source), initialize);
            LastError = worker.LastError;
        }

        public void Write(Exception exception, string message = null, FoundationLogLevel level = FoundationLogLevel.Error,
            string source = null, bool initialize = false, string fileName = null)
        {
            var text = string.IsNullOrWhiteSpace(message)
                ? exception?.ToString()
                : message + Environment.NewLine + exception;
            Write(text, level, source, initialize, fileName);
        }

        public void Flush(string fileName = null)
        {
            if (fileName == null)
            {
                foreach (var worker in _workers.Values)
                {
                    worker.Flush();
                    LastError = worker.LastError;
                }
                return;
            }

            var fullFileName = ResolveLogFileName(fileName);
            if (_workers.TryGetValue(fullFileName, out var matchingWorker))
            {
                matchingWorker.Flush();
                LastError = matchingWorker.LastError;
            }
        }

        public void Rotate(string fileName = null)
        {
            var fullFileName = ResolveLogFileName(fileName);
            var worker = _workers.GetOrAdd(fullFileName, name => new LogFileWorker(name, GetOptionsForFile(name)));
            worker.Rotate();
            LastError = worker.LastError;
        }

        private FoundationLogOptions GetOptionsForFile(string fullFileName)
        {
            return _options.TryGetValue(fullFileName, out var options)
                ? options
                : DefaultOptions;
        }

        private static void CopyOptions(FoundationLogOptions source, FoundationLogOptions target)
        {
            target.MaxFileSizeBytes = source.MaxFileSizeBytes;
            target.MaxRotatedFiles = source.MaxRotatedFiles;
            target.MaxRotatedFileAge = source.MaxRotatedFileAge;
            target.PruneOnRotate = source.PruneOnRotate;
        }

        private static string FormatEntry(string message, FoundationLogLevel level, string source)
        {
            var sourceText = string.IsNullOrWhiteSpace(source) ? "" : $" [{source}]";
            return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}]{sourceText}: {message}";
        }
    }
}
