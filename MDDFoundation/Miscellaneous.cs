using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace MDDFoundation
{
    public enum Direction
    {
        None = 0,
        Up,
        Down,
        Left,
        Right
    }

    public class FileCopyProgress
    {
        public string FileName { get; set; }
        public long FileSizeBytes { get; set; }
        public string OperationDuring { get; set; } = "Copying";
        public string OperationComplete { get; set; } = "Copy";
        public byte[] Hash { get; set; }
        public DateTime StartTime { get; set; }
        private Stopwatch stopwatch = null;
        public Stopwatch Stopwatch
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
                return (BytesCopied / 1024.0 / 1024.0) / Stopwatch.Elapsed.TotalSeconds;
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
            if (p < 1)
                return $"{OperationDuring} {FileName} - {p * 100:N1}% - {RateMBPerSec:N1}MB/s...";
            else if (IsCompleted)
                return $"{OperationComplete} of {FileName} complete in {Stopwatch.Elapsed} - {RateMBPerSec:N1}MB/s";
            else
                return $"{OperationComplete} of {FileName} finishing -  {Stopwatch.Elapsed} - {RateMBPerSec:N1}MB/s";

        }
    }
    public enum ShowWindowEnum
    {
        Hide = 0,
        ShowNormal = 1, ShowMinimized = 2, ShowMaximized = 3,
        Maximize = 3, ShowNormalNoActivate = 4, Show = 5,
        Minimize = 6, ShowMinNoActivate = 7, ShowNoActivate = 8,
        Restore = 9, ShowDefault = 10, ForceMinimized = 11
    };
    public enum State
    {
        Default, Modified, Original
    }
    public static class Foundation
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, ShowWindowEnum flags);
        public static bool ProcessIsRunning(bool activate = true, bool allowwithdebugger = true)
        {
            Process running = null;
            Process current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id != current.Id && current.MainModule.FileName == p.MainModule.FileName)
                    running = p;
            }

            if ((!allowwithdebugger || !Debugger.IsAttached) && running != null)
            {
                if (activate)
                {
                    ShowWindow(running.MainWindowHandle, ShowWindowEnum.Restore);
                    SetForegroundWindow(running.MainWindowHandle);
                }
                return true;
            }
            else
            {
                return false;
            }
        }
        public static bool TryParseDateTime(string dateString, out DateTime dt)
        {
            dateString = dateString.Trim();
            dateString = dateString.Trim('•');

            if (dateString.Length > 20 && dateString.Contains("(") && dateString.Contains(")"))
            {
                dateString = TextBetween(dateString, "(", ")");
            }


            // First, try to parse the date using the regular DateTime.TryParse
            if (DateTime.TryParse(dateString, out dt))
            {
                if (dateString.Contains(dt.Day.ToString()))
                    return true;
            }
            if (dateString.Length > 9 && dateString.Substring(9,1) == ":")
            {
                if (DateTime.TryParse($"{dateString.Substring(0, 6)}, {DateTime.Now.Year}", out dt))
                {
                    if (TimeSpan.TryParse(dateString.Substring(7), out TimeSpan ts))
                    {
                        dt = dt + ts;
                    }
                    return true;
                }
            }


            // If regular parsing fails, remove the suffix from the day part
            string cleanedDateString = Regex.Replace(dateString, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1");

            // Define the format of the cleaned date string
            string format = "d MMM yyyy";
            CultureInfo provider = CultureInfo.InvariantCulture;

            // Try to parse the cleaned date string
            return DateTime.TryParseExact(cleanedDateString, format, provider, DateTimeStyles.None, out dt);
        }
        public static double Subtract(this Point pnt1, Point pnt2)
        {
            return Math.Sqrt((pnt1.X - pnt2.X) * (pnt1.X - pnt2.X) + (pnt1.Y - pnt2.Y) * (pnt1.Y - pnt2.Y));
        }
        public static string TextBetween(string SearchString, string BeforeStr, string AfterStr)
        {
            if (String.IsNullOrWhiteSpace(SearchString))
                return null;
            int TmpIndex = SearchString.IndexOf(BeforeStr, StringComparison.OrdinalIgnoreCase);
            if (TmpIndex == -1)
                return null;
            else
            {
                TmpIndex = TmpIndex + BeforeStr.Length;
                int AfterIndex = SearchString.IndexOf(AfterStr, TmpIndex, StringComparison.OrdinalIgnoreCase);
                if (AfterIndex == -1)
                    return SearchString.Substring(TmpIndex);
                else
                    return SearchString.Substring(TmpIndex, AfterIndex - TmpIndex);
            }
        }
        public static void ForEach<T>(this IEnumerable<T> source, Action<T> action)
        {
            foreach (T element in source)
            {
                action(element);
            }
        }
        public static async Task<byte[]> ReadFileHashAsync(FileInfo tgt, CancellationToken token, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, Func<FileCopyProgress, bool> suspenduntil = null)
        {
            if (!tgt.Exists) throw new IOException($"ReadFileHash: file {tgt.FullName} does not exist");
            FileCopyProgress readprogress = null;
            try
            {
                if (progresscallback != null)
                {
                    if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                    readprogress = new FileCopyProgress { OperationDuring = "Reading hash for", OperationComplete = "Hash read", FileSizeBytes = tgt.Length, FileName = tgt.Name, Stopwatch = Stopwatch.StartNew() };
                }

                using (FileStream fs = new FileStream(tgt.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 8192))
                {
                    using (var hashalg = new SHA1CryptoServiceProvider())
                    {
                        return await hashalg.ComputeHashAsync(fs, token, readprogress, progresscallback, progressreportinterval, suspenduntil);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    if (progresscallback != null)
                    {
                        readprogress.Cancelled = true;
                        progresscallback(readprogress);
                        return null;
                    }
                }
                throw new Exception($"ReadFileHash: error with file {tgt.FullName} - see innerexception", ex);
            }
        }
        public static async Task<byte[]> ReadFileHashFragmentAsync(FileInfo tgt, int fileindex, int breakupsizemb, CancellationToken token, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, Func<FileCopyProgress, bool> suspenduntil = null)
        {
            if (!tgt.Exists) throw new IOException($"ReadFileHash: file {tgt.FullName} does not exist");
            FileCopyProgress readprogress = null;
            try
            {
                if (progresscallback != null)
                {
                    if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                    if (breakupsizemb == 0)
                        readprogress = new FileCopyProgress { OperationDuring = "Reading hash for", OperationComplete = "Hash read", FileSizeBytes = tgt.Length, FileName = $"{tgt.Name}", Stopwatch = Stopwatch.StartNew() };
                    else
                    {
                        var breakupfiles = BreakupFiles.NumBreakupFiles(tgt.Length, breakupsizemb);
                        readprogress = new FileCopyProgress { OperationDuring = "Reading hash for", OperationComplete = "Hash read", FileSizeBytes = tgt.Length, FileName = $"{tgt.Name} ({fileindex}/{breakupfiles})", Stopwatch = Stopwatch.StartNew() };
                    }
                }

                using (FileStream fs = new FileStream(tgt.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 8192))
                {
                    long seekpos = (Convert.ToInt64(fileindex) - 1) * Convert.ToInt64(breakupsizemb) * 1024 * 1024;
                    fs.Seek(seekpos, SeekOrigin.Begin);
                    using (var hashalg = new SHA1CryptoServiceProvider())
                    {
                        return await hashalg.ComputeHashAsync(fs, token, readprogress, progresscallback, progressreportinterval, suspenduntil, breakupsizemb);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    if (progresscallback != null)
                    {
                        readprogress.Cancelled = true;
                        progresscallback(readprogress);
                        return null;
                    }
                }
                throw new Exception($"ReadFileHash: error with file {tgt.FullName} - see innerexception", ex);
            }
        }
        public static async Task<byte[]> ComputeHashAsync(this HashAlgorithm hash, Stream inputStream, CancellationToken token, FileCopyProgress progress, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, Func<FileCopyProgress, bool> suspenduntil = null, int lengthmb = 0)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));
            if (inputStream == null) throw new ArgumentNullException(nameof(inputStream));

            hash.Initialize();
            const int BufferSize = 1024 * 1024;
            var buffer = new byte[BufferSize];
            long streamLength;
            if (lengthmb == 0)
                streamLength = inputStream.Length;
            else
                streamLength = inputStream.Position + lengthmb * 1024 * 1024;

            if (streamLength > inputStream.Length)
                streamLength = inputStream.Length;

            var last = TimeSpan.Zero;

            if (suspenduntil != null)
            {
                if (!suspenduntil(progress))
                {
                    if (progresscallback != null)
                    {
                        progress.Queued = true;
                        progresscallback(progress);
                    }
                    do
                    {
                        if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                        await Task.Delay(progressreportinterval);
                        progresscallback?.Invoke(progress);
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    } while (!suspenduntil(progress));
                    if (progresscallback != null)
                    {
                        progress.Queued = false;
                        progress.Stopwatch = Stopwatch.StartNew();
                    }
                }
            }

            while (!token.IsCancellationRequested)
            {
                if (progresscallback != null && (progress.Stopwatch.Elapsed - last) > progressreportinterval)
                {
                    last = progress.Stopwatch.Elapsed;
                    progresscallback(progress);
                }

                var read = await inputStream.ReadAsync(buffer, 0, BufferSize).ConfigureAwait(false);
                if (inputStream.Position == streamLength)
                {
                    hash.TransformFinalBlock(buffer, 0, read);
                    break;
                }
                hash.TransformBlock(buffer, 0, read, default(byte[]), default(int));
                if (progresscallback != null) progress.BytesCopied += read;
            }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (progresscallback != null)
            {
                progress.BytesCopied = progress.FileSizeBytes;
                progress.Hash = hash.Hash;
                progresscallback(progress);
            }
            return hash.Hash;
        }
        public static async Task<byte[]> CopyToAsync(this FileInfo file, FileInfo destination, bool overwrite, CancellationToken token, bool MoveFile = false, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, Func<FileCopyProgress, bool> suspenduntil = null, bool computehash = false)
        {
            FileInfo tmpfile = null;
            FileCopyProgress copyprogress = null;
            long len = file.Length;
            if (progresscallback != null)
            {
                if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew() };
            }            
            byte[] finalhash = null;
            bool toolatetocancel = false;
            try
            {
                if (!file.Exists) throw new IOException($"Source file {file.FullName} does not exist");
                if (destination.Exists && !overwrite) throw new IOException($"Destination file {destination.FullName} exists (and overwrite was not specified)");

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
                bool swap = false;
                Task writer = null;
                tmpfile = new FileInfo(Path.Combine(destination.DirectoryName, Guid.NewGuid().ToString().Replace("-", "") + ".tmp"));



                if (suspenduntil != null)
                {
                    if (!suspenduntil(copyprogress))
                    {
                        if (progresscallback != null)
                        {
                            copyprogress.Queued = true;
                            progresscallback(copyprogress);
                        }
                        do
                        {
                            await Task.Delay(progressreportinterval);
                            progresscallback?.Invoke(copyprogress);
                            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        } while (!suspenduntil(copyprogress));
                        copyprogress.Queued = false;
                        copyprogress.Stopwatch = Stopwatch.StartNew();
                    }
                }

                using (var hash = new SHA1CryptoServiceProvider())
                using (var source = file.OpenRead())
                using (var dest = tmpfile.OpenWrite())
                {
                    if (computehash) hash.Initialize();
                    dest.SetLength(source.Length);
                    int read;
                    for (long size = 0; size < len; size += read)
                    {
                        if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                        {
                            last = copyprogress.Stopwatch.Elapsed;
                            progresscallback(copyprogress);
                        }
                        read = await source.ReadAsync(swap ? buffer : buffer2, 0, bufferSize).ConfigureAwait(false);
                        if (computehash)
                        {
                            if (source.Position == len)
                            {
                                hash.TransformFinalBlock(swap ? buffer : buffer2, 0, read);
                                if (progresscallback != null) copyprogress.Hash = hash.Hash;
                                finalhash = hash.Hash;
                            }
                            else
                                hash.TransformBlock(swap ? buffer : buffer2, 0, read, default(byte[]), default(int));
                        }
                        writer?.Wait();
                        writer = dest.WriteAsync(swap ? buffer : buffer2, 0, read);
                        swap = !swap;
                        if (progresscallback != null) copyprogress.BytesCopied += read;
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    }
                    writer?.Wait();
                }

                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                toolatetocancel = true;
                if (destination.Exists) destination.Delete();
                tmpfile.MoveTo(destination.FullName);
                var waitloop = 0;
                while (!destination.Exists)
                {
                    waitloop++;
                    await Task.Delay(100).ConfigureAwait(false);
                    destination.Refresh();
                    if (waitloop > 10) throw new Exception($"CopyToExt: file {file.Name} - copy has ostensibly completed, but it did not appear in the destination");
                }
                File.SetCreationTime(destination.FullName, file.CreationTime);
                File.SetLastWriteTime(destination.FullName, file.LastWriteTime);
                File.SetLastAccessTime(destination.FullName, file.LastAccessTime);
                if (MoveFile) file.Delete();
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                    throw ex;
            }
            finally
            {
                if (!toolatetocancel && tmpfile != null && tmpfile.Exists) tmpfile.Delete();

                if (progresscallback != null)
                {
                    if (!toolatetocancel && token.IsCancellationRequested)
                        copyprogress.Cancelled = true;
                    else
                        copyprogress.BytesCopied = len;
                    progresscallback(copyprogress);
                }
            }
            return finalhash;
        }
        public static async Task<byte[]> CopyToAsync(this FileInfo file, FileInfo[] destinations, bool overwrite, CancellationToken token, bool MoveFile = false, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, Func<FileCopyProgress, bool> suspenduntil = null, bool computehash = false)
        {
            string tmpfilename = Guid.NewGuid().ToString().Replace("-", "") + ".tmp";
            Tuple<FileInfo, FileInfo>[] tmpfiles = destinations.Select(x => new Tuple<FileInfo,FileInfo>(new FileInfo(Path.Combine(x.DirectoryName, tmpfilename)), x)).ToArray();
            FileCopyProgress copyprogress = null;
            long len = file.Length;
            if (progresscallback != null)
            {
                if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew() };
            }
            byte[] finalhash = null;
            bool toolatetocancel = false;
            FileStream[] tmpdestinations = null;
            try
            {
                if (!file.Exists) throw new IOException($"Source file {file.FullName} does not exist");

                destinations.ForEach(x => 
                {
                    if (!x.Directory.Exists) throw new IOException($"Destination folder {x.DirectoryName} does not exist or is not available");
                    if (!overwrite && x.Exists) throw new IOException($"Destination file {x.FullName} exists (and overwrite was not specified)"); 
                });

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
                bool swap = false;
                Task[] writers = null;



                if (suspenduntil != null)
                {
                    if (!suspenduntil(copyprogress))
                    {
                        if (progresscallback != null)
                        {
                            copyprogress.Queued = true;
                            progresscallback(copyprogress);
                        }
                        do
                        {
                            await Task.Delay(progressreportinterval);
                            progresscallback?.Invoke(copyprogress);
                            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        } while (!suspenduntil(copyprogress));
                        copyprogress.Queued = false;
                        copyprogress.Stopwatch = Stopwatch.StartNew();
                    }
                }

                using (var hash = new SHA1CryptoServiceProvider())
                using (var source = file.OpenRead())
                {
                    tmpdestinations = tmpfiles.Select(x => x.Item1.OpenWrite()).ToArray();
                    if (computehash) hash.Initialize();

                    tmpdestinations.ForEach(x => x.SetLength(source.Length));

                    int read;
                    for (long size = 0; size < len; size += read)
                    {
                        if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                        {
                            last = copyprogress.Stopwatch.Elapsed;
                            progresscallback(copyprogress);
                        }
                        read = await source.ReadAsync(swap ? buffer : buffer2, 0, bufferSize).ConfigureAwait(false);
                        if (computehash)
                        {
                            if (source.Position == len)
                            {
                                hash.TransformFinalBlock(swap ? buffer : buffer2, 0, read);
                                if (progresscallback != null) copyprogress.Hash = hash.Hash;
                                finalhash = hash.Hash;
                            }
                            else
                                hash.TransformBlock(swap ? buffer : buffer2, 0, read, default(byte[]), default(int));
                        }
                        if (writers != null) Task.WaitAll(writers);

                        writers = tmpdestinations.Select(x => x.WriteAsync(swap ? buffer : buffer2, 0, read)).ToArray();

                        swap = !swap;
                        if (progresscallback != null) copyprogress.BytesCopied += read;
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    }
                    if (writers != null) Task.WaitAll(writers);
                    tmpdestinations.ForEach(x => x.Close());
                }

                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                toolatetocancel = true;

                destinations.ForEach(x =>  { if (x.Exists) x.Delete(); });
                tmpfiles.ForEach(x => x.Item1.MoveTo(x.Item2.FullName));


                destinations.ForEach(async x =>
                {
                    var waitloop = 0;
                    while (!x.Exists)
                    {
                        waitloop++;
                        await Task.Delay(100).ConfigureAwait(false);
                        x.Refresh();
                        if (waitloop > 10) throw new Exception($"CopyToAsync: file {file.Name} - copy has ostensibly completed, but it did not appear in the destination '{x.FullName}'");
                    }
                    File.SetCreationTime(x.FullName, file.CreationTime);
                    File.SetCreationTime(x.FullName, file.CreationTime);
                    File.SetLastWriteTime(x.FullName, file.LastWriteTime);
                    File.SetLastAccessTime(x.FullName, file.LastAccessTime);
                    if (MoveFile) file.Delete();
                });
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                    throw ex;
            }
            finally
            {
                tmpdestinations?.ForEach(x => { if (x != null) x.Close(); });

                if (!toolatetocancel)
                {
                    tmpfiles.ForEach(x => { if (x.Item1.Exists) x.Item1.Delete(); });
                }
                if (progresscallback != null)
                {
                    if (!toolatetocancel && token.IsCancellationRequested)
                        copyprogress.Cancelled = true;
                    else
                        copyprogress.BytesCopied = len;
                    progresscallback(copyprogress);
                }
            }
            return finalhash;
        }
        public static async Task<List<FileInfo>> GetFilesAsync(this DirectoryInfo dir, string searchpattern = null, SearchOption searchoption = default)
        {
            List<FileInfo> files = new List<FileInfo>();
            if (searchpattern == null) searchpattern = "*.*";
            await Task.Run(() =>
            {
                files = dir.GetFiles(searchpattern, searchoption).ToList();
            });
            return files;
        }
        public static string GenerateBase64EncryptionKey(int keySizeInBytes = 32)
        {
            if (keySizeInBytes != 16 && keySizeInBytes != 24 && keySizeInBytes != 32)
                throw new ArgumentException("Key size must be 128, 192, or 256 bits.", nameof(keySizeInBytes));

            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                byte[] keyBytes = new byte[keySizeInBytes];
                rng.GetBytes(keyBytes);
                return Convert.ToBase64String(keyBytes);
            }
        }
        public static async Task<string> EncryptAndCopyAsync(string sourceFile, string destinationFolder, string base64EncryptionKey)
        {
            byte[] encryptionKey = Convert.FromBase64String(base64EncryptionKey);
            byte[] iv;
            const int BufferSize = 1024 * 1024;
            bool swap = false;

            var file = new FileInfo(sourceFile);

            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = encryptionKey;
                iv = aesAlg.IV;

                var tmpfile = Path.Combine(destinationFolder, Guid.NewGuid().ToString().Replace("-", "") + ".tmp");

                ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, iv);

                using (FileStream sourceStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true))
                using (FileStream destinationStream = new FileStream(tmpfile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
                using (CryptoStream cryptoStream = new CryptoStream(destinationStream, encryptor, CryptoStreamMode.Write))
                {
                    await destinationStream.WriteAsync(aesAlg.IV, 0, aesAlg.IV.Length);

                    byte[] buffer1 = new byte[BufferSize];
                    byte[] buffer2 = new byte[BufferSize];
                    int bytesRead;

                    bytesRead = await sourceStream.ReadAsync(buffer1, 0, buffer1.Length);
                    while (bytesRead > 0)
                    {
                        Task<int> readTask = sourceStream.ReadAsync(swap ? buffer1 : buffer2, 0, buffer2.Length);
                        await cryptoStream.WriteAsync(swap ? buffer2 : buffer1, 0, bytesRead);
                        bytesRead = await readTask;
                        swap = !swap;
                    }
                }

                var filename = file.Name;
                byte[] filenameBytes = Encoding.UTF8.GetBytes(filename);
                byte[] encryptedBytes = aesAlg.CreateEncryptor().TransformFinalBlock(filenameBytes, 0, filenameBytes.Length);
                //byte[] result = new byte[aesAlg.IV.Length + encryptedBytes.Length];
                //aesAlg.IV.CopyTo(result, 0);
                //encryptedBytes.CopyTo(result, aesAlg.IV.Length);
                string encryptedFileName = Convert.ToBase64String(encryptedBytes).Replace('/', '_').Replace('+', '-');
                string destinationFile = Path.Combine(destinationFolder, encryptedFileName + ".dat");

                File.Move(tmpfile, destinationFile);
                File.SetCreationTime(destinationFile, file.CreationTime);
                File.SetLastWriteTime(destinationFile, file.LastWriteTime);
                File.SetLastAccessTime(destinationFile, file.LastAccessTime);
                return destinationFile;
            }
        }
        public static async Task<string> DecryptAndCopyAsync(string encryptedFile, string destinationFolder, string base64EncryptionKey)
        {
            byte[] encryptionKey = Convert.FromBase64String(base64EncryptionKey);
            byte[] iv;
            const int BufferSize = 1024 * 1024;
            bool swap = false;

            var file = new FileInfo(encryptedFile);

            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = encryptionKey;
                var tmpfile = Path.Combine(destinationFolder, Guid.NewGuid().ToString().Replace("-", "") + ".tmp");
                ICryptoTransform decryptor = null;

                using (FileStream sourceStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true))
                {
                    iv = new byte[aesAlg.IV.Length];
                    await sourceStream.ReadAsync(iv, 0, iv.Length);
                    aesAlg.IV = iv;
                    decryptor = aesAlg.CreateDecryptor(aesAlg.Key, iv);

                    using (FileStream destinationStream = new FileStream(tmpfile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
                    using (CryptoStream cryptoStream = new CryptoStream(sourceStream, decryptor, CryptoStreamMode.Read))
                    {
                        byte[] buffer1 = new byte[BufferSize];
                        byte[] buffer2 = new byte[BufferSize];
                        int bytesRead;

                        bytesRead = await cryptoStream.ReadAsync(buffer1, 0, buffer1.Length);
                        while (bytesRead > 0)
                        {
                            Task<int> readTask = cryptoStream.ReadAsync(swap ? buffer1 : buffer2, 0, buffer2.Length);
                            await destinationStream.WriteAsync(swap ? buffer2 : buffer1, 0, bytesRead);
                            bytesRead = await readTask;
                            swap = !swap;
                        }
                    }
                }


                var encryptedFilename = file.Name;
                encryptedFilename = encryptedFilename.Substring(0, encryptedFilename.Length - 4);

                byte[] encryptedBytes = Convert.FromBase64String(encryptedFilename.Replace('_', '/').Replace('-', '+'));
                byte[] decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
                string decryptedFileName =  Encoding.UTF8.GetString(decryptedBytes);

                var finalfile = Path.Combine(destinationFolder, decryptedFileName);
                File.Move(tmpfile,finalfile);
                File.SetCreationTime(finalfile, file.CreationTime);
                File.SetLastWriteTime(finalfile, file.LastWriteTime);
                File.SetLastAccessTime(finalfile, file.LastAccessTime);
                return finalfile;
            }
        }
        public static string CopyToMultiBuffer(this FileInfo file, FileInfo destination, bool overwrite, bool MoveFile = false)
        {
            long len = file.Length;
            var control = new int[5];
            var readlength = new int[5];
            const int bufferSize = 1024 * 1024;  //1MB
            var buffers = new List<byte[]> { new byte[bufferSize], new byte[bufferSize], new byte[bufferSize], new byte[bufferSize], new byte[bufferSize] };

            FileCopyProgress copyprogress = new FileCopyProgress { OperationComplete = "MB Copy", FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew() };

            var reader = new Thread(() =>
            {
                int readsequence = 0;
                long size = 0;
                int read;
                using (var source = file.OpenRead())
                {
                    while (size < len)
                    {
                        for (int i = 0; i < control.Length; i++)
                        {
                            if (control[i] == 0)
                            {
                                read = source.Read(buffers[i], 0, bufferSize);
                                readlength[i] = read;
                                readsequence++;
                                control[i] = readsequence;
                                size += read;
                            }
                        }
                    }
                }
            });
            reader.Start();

            var tmpfile = new FileInfo(Path.Combine(destination.DirectoryName, Guid.NewGuid().ToString().Replace("-", "") + ".tmp"));

            using (var dest = tmpfile.OpenWrite())
            {
                int writesequence = 1;
                long size = 0;
                while (size < len)
                {
                    for (int i = 0; i < control.Length; i++)
                    {
                        if (control[i] == writesequence)
                        {
                            dest.Write(buffers[i], 0, readlength[i]);
                            size += readlength[i];
                            control[i] = 0;
                            writesequence++;
                        }
                    }
                }
            }

            reader.Join();

            tmpfile.MoveTo(destination.FullName);
            var waitloop = 0;
            while (!destination.Exists)
            {
                waitloop++;
                Thread.Sleep(100);
                destination.Refresh();
                if (waitloop > 10) throw new Exception($"CopyToExt: file {file.Name} - copy has ostensibly completed, but it did not appear in the destination");
            }
            File.SetCreationTime(destination.FullName, file.CreationTime);
            File.SetLastWriteTime(destination.FullName, file.LastWriteTime);
            File.SetLastAccessTime(destination.FullName, file.LastAccessTime);
            if (MoveFile) file.Delete();
            copyprogress.BytesCopied = len;
            return copyprogress.ToString();
        }
        public static void BreakupFile(this FileInfo file, DirectoryInfo destination, int filesizemb)
        {
            int CopyBufferSize = 8 * 1024 * 1024;
            byte[] InBuffer = new byte[CopyBufferSize + 1];
            long TotalBlocks = (file.Length / CopyBufferSize);
            long FileSizeBlocks = filesizemb / 8;
            long FileSizeBytes = CopyBufferSize * FileSizeBlocks;
            int TotalFiles = (int)Math.Ceiling(Convert.ToDouble(file.Length) / Convert.ToDouble(FileSizeBytes));
        }
        public static string LogFileFullName(string filename)
        {
            DirectoryInfo logfiledir = (new FileInfo(Assembly.GetExecutingAssembly().Location)).Directory;
            return Path.Combine(logfiledir.FullName, filename);
        }
        public static void Log(string LogStr, bool Initialize = false, string filename = "Foundation_log.txt")
        {
            string CurLogFile = LogFileFullName(filename);
            bool Finished = false;
            bool WriteHeader = !File.Exists(CurLogFile) || LogStr == "";
            if (!WriteHeader && Initialize)
            {
                File.Delete(CurLogFile);
                WriteHeader = true;
            }
            while (!Finished)
            {
                try
                {
                    using (StreamWriter writer = File.AppendText(CurLogFile))
                    {
                        if (WriteHeader)
                            writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " -- Log File");
                        if (LogStr != "")
                            writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + ": " + LogStr);
                    }
                    Finished = true;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("because it is being used by another process"))
                        Thread.Sleep(50);
                    else
                        throw ex;
                }

            }
        }
        public static void Retry(Action action, int numretries = 10, int delayms = 1000, Action<Exception, int> interimexception = null)
        {
            while (numretries > 0)
            {
                numretries -= 1;
                try
                {
                    action.Invoke();
                    return;
                }
                catch (Exception ex)
                {
                    if (numretries > 0)
                    {
                        Thread.Sleep(delayms);
                        interimexception?.Invoke(ex,numretries);
                    }
                    else
                        throw ex;
                }
            }
        }
        public static void SynchronizedInvoke(this ISynchronizeInvoke sync, Action action)
        {
            if (!sync.InvokeRequired)
            {
                action();
                return;
            }
            sync.Invoke(action, new object[] { });
        }
        public static void SyncSet(this ISynchronizeInvoke sync, object value, object property = null)
        {
            PropertyInfo pinfo;
            if (property == null)
                pinfo = sync.GetType().GetProperty("Text");
            else if (property is string)
                pinfo = sync.GetType().GetProperty(property.ToString());
            else
                pinfo = property as PropertyInfo;
            sync.SynchronizedInvoke(() => pinfo.SetValue(sync, value));
        }
        public static string Replace(this string str, string oldValue, string newValue, StringComparison comparison)
        {
            StringBuilder sb = new StringBuilder();

            int previousIndex = 0;
            int index = str.IndexOf(oldValue, comparison);
            while (index != -1)
            {
                sb.Append(str.Substring(previousIndex, index - previousIndex));
                sb.Append(newValue);
                index += oldValue.Length;

                previousIndex = index;
                index = str.IndexOf(oldValue, index, comparison);
            }
            sb.Append(str.Substring(previousIndex));

            return sb.ToString();
        }
        public static bool Contains(this string source, string toCheck, StringComparison comp)
        {
            return source?.IndexOf(toCheck, comp) >= 0;
        }
        [DllImport("user32", EntryPoint = "OpenDesktopA", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern Int32 OpenDesktop(string lpszDesktop, Int32 dwFlags, bool fInherit, Int32 dwDesiredAccess);
        [DllImport("user32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern Int32 CloseDesktop(Int32 hDesktop);
        [DllImport("user32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern Int32 SwitchDesktop(Int32 hDesktop);
        [DllImport("user32", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        public static extern void LockWorkstation();
        public static bool IsWorkstationLocked()
        {
            const int DESKTOP_SWITCHDESKTOP = 256;
            int hwnd = -1;
            int rtn = -1;

            hwnd = OpenDesktop("Default", 0, false, DESKTOP_SWITCHDESKTOP);

            if (hwnd != 0)
            {
                rtn = SwitchDesktop(hwnd);
                if (rtn == 0)
                {
                    // Locked
                    CloseDesktop(hwnd);
                    return true;
                }
                else
                {
                    // Not locked
                    CloseDesktop(hwnd);
                }
            }
            else
            {
                // Error: "Could not access the desktop..."
            }
            return false;
        }
        public static IEnumerable<TResult> FullOuterJoin<TA, TB, TKey, TResult>(
        this IEnumerable<TA> a,
        IEnumerable<TB> b,
        Func<TA, TKey> selectKeyA,
        Func<TB, TKey> selectKeyB,
        Func<TA, TB, TKey, TResult> projection,
        TA defaultA = default(TA),
        TB defaultB = default(TB),
        IEqualityComparer<TKey> cmp = null)
        {
            cmp = cmp ?? EqualityComparer<TKey>.Default;
            var alookup = a.ToLookup(selectKeyA, cmp);
            var blookup = b.ToLookup(selectKeyB, cmp);

            var keys = new HashSet<TKey>(alookup.Select(p => p.Key), cmp);
            keys.UnionWith(blookup.Select(p => p.Key));

            var join = from key in keys
                       from xa in alookup[key].DefaultIfEmpty(defaultA)
                       from xb in blookup[key].DefaultIfEmpty(defaultB)
                       select projection(xa, xb, key);

            return join;
        }
        public static void Shuffle<T>(this IList<T> list, Random rng = null)
        {
            if (rng == null) rng = new Random();

            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
        public static string AddCSV(this string str, string addtext)
        {
            if (str == null) return addtext;
            if (!str.Split(',').Select(x => x.Trim()).Any(x => x.Equals(addtext,StringComparison.OrdinalIgnoreCase))) return str += $", {addtext}";
            //else if (!str.Contains(','))
            //{
            //    if (!str.Equals(addtext, StringComparison.OrdinalIgnoreCase)) return str += $", {addtext}";
            //}
            //else
            //{
            //}
            return str;
        }
        public static T NullIf<T>(this T value, T compareValue) where T : class
        {
            return value == compareValue ? null : value;
        }
        public static string NullIf(this string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        public static bool IsSameAs<T>(this T ref1, T ref2)
        {
            foreach (var prop in typeof(T).GetProperties())
                if (!StructuralComparisons.StructuralEqualityComparer.Equals(prop.GetValue(ref2), prop.GetValue(ref1)))
                    return false;
            return true;
        }
    }
    public class StringCIEqualityComparer : IEqualityComparer<string>
    {
        public bool Equals(string x, string y)
        {
            return x.Equals(y, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(string obj)
        {
            return obj.GetHashCode();
        }
    }
}
