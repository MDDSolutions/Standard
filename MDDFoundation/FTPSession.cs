using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Threading;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MDDFoundation
{
    public class FTPSession
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string UserName { get; set; }
        public string Password { private get; set; }
        private string currentfolder = null;
        public string CurrentFolder
        {
            get => currentfolder;
            set
            {
                currentfolder = NormalizeFolder(value);
            }
        }


        private ConcurrentDictionary<string, RemoteFileList> filelists = new ConcurrentDictionary<string, RemoteFileList>();
        public int DebugLevel { get; set; } = 1;
        public static string NormalizeFolder(string folder)
        {
            return folder?.TrimStart('/').TrimEnd('/');
        }
        private FtpWebRequest GetRequest(string method, string filename, string folder)
        {
            if (folder == null) folder = CurrentFolder;

            var uri = $"ftp://{Host}:{Port}/{NormalizeFolder(folder)}/";

            if (!string.IsNullOrWhiteSpace(filename))
                uri = uri + filename;

            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = method; //WebRequestMethods.Ftp.ListDirectoryDetails;
            request.Timeout = 100000;
            request.ReadWriteTimeout = 300000;

            request.Credentials = new NetworkCredential(UserName, Password);

            return request;
        }
        public async Task<bool> FileExistsAsync(string filename, string folder)
        {
            bool exists = false;
            if (folder == null) folder = CurrentFolder;
            folder = NormalizeFolder(folder);
            var request = GetRequest(WebRequestMethods.Ftp.GetDateTimestamp, filename, folder);
            try
            {
                await request.GetResponseAsync().ConfigureAwait(false);
                exists = true;
            }
            catch (WebException ex)
            {
                FtpWebResponse response = (FtpWebResponse)ex.Response;
                if (response.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                {
                    throw ex;
                }
            }
            return exists;
        }
        public bool FileExists(string filename, string folder = null)
        {
            bool exists = false;
            if (folder == null) folder = CurrentFolder;
            folder = NormalizeFolder(folder);
            var request = GetRequest(WebRequestMethods.Ftp.GetDateTimestamp, filename, folder);
            try
            {
                request.GetResponse();
                exists = true;
            }
            catch (WebException ex)
            {
                FtpWebResponse response = (FtpWebResponse)ex.Response;
                if (response.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                {
                    throw ex;
                }
            }
            return exists;
        }
        public async Task<IList<FTPFile>> ListAsync(TimeSpan updatethreshold = default, string folder = null)
        {
            if (folder == null) folder = CurrentFolder;
            folder = NormalizeFolder(folder);
            var files = filelists.GetOrAdd(folder, new RemoteFileList());

            if (updatethreshold == default) updatethreshold = TimeSpan.FromSeconds(30);
            if (files.List == null || (DateTime.Now - files.LastUpdated) > updatethreshold)
            {
                var request = GetRequest(WebRequestMethods.Ftp.ListDirectoryDetails, null, folder);

                files.List = new List<FTPFile>();

                using (var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        files.List.Add(FTPFile.FromLine(line));
                    }
                }
                files.LastUpdated = DateTime.Now;
            }
            return files.List.AsReadOnly();
        }
        private ManualResetEventSlim listreset = null;
        public IList<FTPFile> List(TimeSpan updatethreshold = default, string folder = null)
        {
            if (updatethreshold == default) updatethreshold = TimeSpan.FromSeconds(30);
            if (listreset != null)
            {
                StatusUpdate($"UploadFile: List is already being fetched", 4);
                listreset?.Wait();
            }
            if (folder == null) folder = CurrentFolder;
            folder = NormalizeFolder(folder);
            var files = filelists.GetOrAdd(folder, new RemoteFileList());
            if (updatethreshold >= TimeSpan.FromHours(12))
            {
                if (files.List == null) files.List = new List<FTPFile>();
            }
            else if (files.List == null || (DateTime.Now - files.LastUpdated) > updatethreshold)
            {
                listreset = new ManualResetEventSlim();
                try
                {
                    StatusUpdate($"UploadFile: Getting List from Server", 4);
                    var request = GetRequest(WebRequestMethods.Ftp.ListDirectoryDetails, null, folder);

                    files.List = new List<FTPFile>();

                    using (var response = (FtpWebResponse)request.GetResponse())
                    using (var responseStream = response.GetResponseStream())
                    using (var reader = new StreamReader(responseStream))
                    {
                        while (!reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            files.List.Add(FTPFile.FromLine(line));
                        }
                    }
                    files.LastUpdated = DateTime.Now;
                    listreset.Set();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                finally
                {
                    listreset?.Dispose();
                    listreset = null;
                }
            }
            else
            {
                StatusUpdate($"UploadFile: List cache hit", 4);
            }
            return files.List.AsReadOnly();
        }
        public async Task RenameAsync(string from, string to, string folder = null)
        {
            if (folder == null) folder = CurrentFolder;
            folder = NormalizeFolder(folder);
            var request = GetRequest(WebRequestMethods.Ftp.Rename, from, folder);
            request.RenameTo = to;

            using (var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            {
            }
        }
        public void Rename(string from, string to, string folder = null)
        {
            if (folder == null) folder = CurrentFolder;
            folder = NormalizeFolder(folder);
            var request = GetRequest(WebRequestMethods.Ftp.Rename, from, folder);
            request.RenameTo = to;

            StatusUpdate($"{DateTime.Now:HH:mm:ss.fff}: Rename started: {from} -> {to}", 4);
            using (var response = (FtpWebResponse)request.GetResponse())
            {
                StatusUpdate($"{DateTime.Now:HH:mm:ss.fff}: Rename finished: {from} -> {to}", 4);
            }
        }
        public async Task DeleteAsync(string filename, string folder = null)
        {
            var request = GetRequest(WebRequestMethods.Ftp.DeleteFile, filename, folder);

            using (var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            {
            }
        }
        public void Delete(string filename, string folder = null)
        {
            var request = GetRequest(WebRequestMethods.Ftp.DeleteFile, filename, folder);

            using (var response = (FtpWebResponse)request.GetResponse())
            {
            }
        }
        public async Task MoveFileAsync(string filename, string destinationfolder, string sourcefolder = null)
        {
            var request = GetRequest(WebRequestMethods.Ftp.Rename, filename, sourcefolder);
            if (!destinationfolder.EndsWith("/")) destinationfolder = destinationfolder + "/";
            if (!destinationfolder.StartsWith("/")) destinationfolder = "/" + destinationfolder;
            request.RenameTo = $"{destinationfolder}{filename}";
            using (var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            {
            }
        }
        public void MoveFile(string filename, string destinationfolder, string sourcefolder = null)
        {
            var request = GetRequest(WebRequestMethods.Ftp.Rename, filename, sourcefolder);
            if (!destinationfolder.EndsWith("/")) destinationfolder = destinationfolder + "/";
            if (!destinationfolder.StartsWith("/")) destinationfolder = "/" + destinationfolder;
            request.RenameTo = $"{destinationfolder}{filename}";
            using (var response = (FtpWebResponse)request.GetResponse())
            {
            }
        }


        public async Task<FileCopyProgress> UploadFileFragmentAsync(FileInfo file, CancellationToken token, string destinationfolder = null, bool MoveFile = false, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, int breakupmb = 0, int breakupindex = 0, bool processhash = true, double maxmbpersec = 0)
        {
            // this method will overwrite the file if it exists (default functionality for FTP) so check before running if it is important to you
            FileCopyProgress copyprogress = null;
            try
            {
                if (!file.Exists) throw new IOException($"FTPSession.UploadFileFragmentAsync: Source file {file.FullName} does not exist");
                if (breakupindex != 0 && MoveFile) throw new Exception("You can't move the file if you're only transferring a fragment");

                if (destinationfolder == null) destinationfolder = CurrentFolder;
                destinationfolder = NormalizeFolder(destinationfolder);

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
                bool swap = false;
                Task writer = null;

                long breakupbytes = breakupmb * 1024 * 1024;
                int breakupfiles = 1;
                long len = file.Length;
                if (breakupbytes > 0 && breakupbytes < len)
                    breakupfiles = BreakupFiles.NumBreakupFiles(len, breakupmb);

                if (breakupindex > breakupfiles) throw new Exception($"FTPSession.UploadFileFragmentAsync: breakupindex specified for {file.FullName} of {breakupindex} is greater than the number of breakup files available of {breakupfiles}");
                copyprogress = new FileCopyProgress { FileName = file.Name, Stopwatch = Stopwatch.StartNew(), OperationDuring = "Transferring", OperationComplete = "Transfer" };
                long offset = 0;
                if (breakupindex > 0)
                {
                    offset = (breakupindex - 1) * breakupbytes;
                    var left = len - offset;
                    if (left > breakupbytes)
                        len = breakupbytes;
                    else
                        len = left;
                    copyprogress.FileName += $" ({breakupindex}/{breakupfiles})";
                }
                copyprogress.FileSizeBytes = len;

                int msperloop = 0;
                if (maxmbpersec > 0)
                {
                    msperloop = Convert.ToInt32(1000 / (maxmbpersec * 1024 * 1024 / bufferSize)) - 20;
                    if (msperloop <= 0) msperloop = 1;
                }

                //bool targetdelete = false;

                var breakupfilename = new BreakupFileName(breakupindex, breakupfiles, file.Name); // BreakupFiles.BreakupFileName(breakupindex, breakupfiles, file.Name);

                var tmpfile = Guid.NewGuid().ToString().Replace("-", "") + ".tmp";
                var request = GetRequest(WebRequestMethods.Ftp.UploadFile, tmpfile, destinationfolder);

                int currentblockcount = 0;
                long curstart = 0;
                if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);

                using (var hash = new SHA1CryptoServiceProvider())
                using (var source = file.OpenRead())
                using (var dest = await request.GetRequestStreamAsync().ConfigureAwait(false))
                {
                    if (processhash) hash.Initialize();
                    source.Seek(offset, 0);
                    int read;
                    for (long size = 0; size < len; size += read)
                    {
                        if (msperloop > 0) curstart = copyprogress.Stopwatch.ElapsedMilliseconds;
                        currentblockcount++;
                        if (token.IsCancellationRequested) break;
                        if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                        {
                            last = copyprogress.Stopwatch.Elapsed;
                            progresscallback(copyprogress);
                        }
                        read = await source.ReadAsync(swap ? buffer : buffer2, 0, bufferSize).ConfigureAwait(false);
                        if (processhash) hash.TransformBlock(swap ? buffer : buffer2, 0, read, default, default);
                        if (writer != null) await writer.ConfigureAwait(false);
                        writer = dest.WriteAsync(swap ? buffer : buffer2, 0, read);
                        swap = !swap;
                        copyprogress.BytesCopied += read;
                        if (msperloop > 0)
                        {
                            if (currentblockcount % 10 == 0)
                            {
                                var currate = copyprogress.RateMBPerSec;
                                var curratio = currate / maxmbpersec;
                                msperloop = Convert.ToInt32(msperloop * curratio);
                                StatusUpdate($"UploadFile for {file.Name}: rate has been {currate} - msperloop changed to {msperloop} at block count {currentblockcount} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                                if (msperloop <= 0) msperloop = 1;
                            }
                            await Task.Delay(msperloop).ConfigureAwait(false);
                        }
                    }
                    if (writer != null) await writer.ConfigureAwait(false);
                    if (processhash)
                    {
                        hash.TransformFinalBlock(buffer, 0, 0);
                        copyprogress.Hash = hash.Hash;
                    }
                }
                if (progresscallback != null)
                {
                    last = copyprogress.Stopwatch.Elapsed;
                    progresscallback(copyprogress);
                }

                if (token.IsCancellationRequested)
                {
                    try
                    {
                        await DeleteAsync(tmpfile, destinationfolder).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                    return null;
                }
                else
                {
                    //if (targetdelete && breakupindex == 0) // currentbreakupfile == breakupfiles)
                    //    await DeleteAsync(file.Name, destinationfolder).ConfigureAwait(false);

                    if (breakupindex == 0)
                        await RenameAsync(tmpfile, file.Name, destinationfolder).ConfigureAwait(false);
                    else
                        await RenameAsync(tmpfile, breakupfilename.ToString(), destinationfolder).ConfigureAwait(false);

                    if (await FileExistsAsync(file.Name, destinationfolder).ConfigureAwait(false))
                    {
                        var list = filelists.GetOrAdd(destinationfolder, new RemoteFileList());
                        if (!list.List.Any(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                            list.List.Add(new FTPFile { FileName = file.Name, IsDirectory = false });
                        if (MoveFile && breakupindex == 0) // currentbreakupfile == breakupfiles)
                        {
                            file.Delete();
                        }
                    }
                    else if (MoveFile && breakupindex == 0) // currentbreakupfile == breakupfiles)
                    {
                        throw new Exception($"FTPSession.UploadFileAsync: Target file {file.Name} does not exist after apparently successful transfer - not deleting source file");
                    }



                    if (progresscallback != null)
                    {
                        if (copyprogress.BytesCopied != len) throw new Exception("BytesCopied is not the length of the file");
                        copyprogress.IsCompleted = true;
                        progresscallback(copyprogress);
                    }
                }
                return copyprogress;

            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("(425) Can't open data connection"))
                {
                    StatusUpdate($"ERROR in FTPSession.UploadFileFragmentAsync: Could not open data connection for {copyprogress.FileName} - this is a recoverable error if there is retry logic calling this method - BytesCopied: {copyprogress.BytesCopied} / {copyprogress.FileSizeBytes} Complete: {copyprogress.IsCompleted}", 15);
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"ERROR in FTPSession.UploadFileFragmentAsync with status: {copyprogress}, Complete: {copyprogress.IsCompleted}");
                    sb.Append(ex.ToString());
                    StatusUpdate(sb.ToString(), 16);
                }
                copyprogress.IsCompleted = false;
                return copyprogress;
            }
        }
        public async Task<FileCopyProgress> UploadFileAsync(FileInfo file, CancellationToken token, bool overwrite, string destinationfolder = null, bool MoveFile = false, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, int breakupmb = 0, bool processhash = true)
        {
            try
            {
                if (!file.Exists) throw new IOException($"FTPSession.UploadFileAsync: Source file {file.FullName} does not exist");

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
                bool swap = false;
                long len = file.Length;
                Task writer = null;

                long breakupbytes = breakupmb * 1024 * 1024;
                int breakupfiles = 1;
                if (breakupbytes > 0 && breakupbytes < len)
                    breakupfiles = Convert.ToInt32(Math.Ceiling(len / (double)breakupbytes));


                bool targetdelete = false;

                if (destinationfolder == null) destinationfolder = CurrentFolder;
                destinationfolder = NormalizeFolder(destinationfolder);

                var list = (await ListAsync(default, destinationfolder).ConfigureAwait(false)).ToList();
                if (list.Exists(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!overwrite)
                        throw new Exception($"FTPSession.UploadFileAsync: Target file {file.Name} exists - overwrite was not specified");
                    else
                        targetdelete = true;
                }

                FileCopyProgress copyprogress = null;
                if (progresscallback != null)
                {
                    if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                    copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew(), OperationDuring = "Transferring", OperationComplete = "Transfer" };
                }


                for (int currentbreakupfile = 1; currentbreakupfile <= breakupfiles; currentbreakupfile++)
                {
                    var breakupfilename = $"{currentbreakupfile:00000}.{breakupfiles:00000}.{file.Name}.breakup";
                    if (breakupfiles > 1) list = (await ListAsync(TimeSpan.FromTicks(-1), destinationfolder).ConfigureAwait(false)).ToList();
                    if (breakupfiles <= 1 || !list.Exists(x => x.FileName.Equals(breakupfilename, StringComparison.OrdinalIgnoreCase)))
                    {
                        var tmpfile = Guid.NewGuid().ToString().Replace("-", "") + ".tmp";
                        var request = GetRequest(WebRequestMethods.Ftp.UploadFile, tmpfile, destinationfolder);

                        int currentblockcount = 0;

                        using (var hash = new SHA1CryptoServiceProvider())
                        using (var source = file.OpenRead())
                        using (var dest = await request.GetRequestStreamAsync().ConfigureAwait(false))
                        {
                            //dest.SetLength(source.Length);
                            if (processhash) hash.Initialize();
                            source.Seek((currentbreakupfile - 1) * breakupbytes, 0);
                            int read;
                            for (long size = 0; size < len; size += read)
                            {
                                currentblockcount++;
                                if (token.IsCancellationRequested) break;
                                if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                                {
                                    last = copyprogress.Stopwatch.Elapsed;
                                    progresscallback(copyprogress);
                                }
                                read = await source.ReadAsync(swap ? buffer : buffer2, 0, bufferSize).ConfigureAwait(false);
                                if (processhash)
                                {
                                    if (copyprogress != null && source.Position == len)
                                    {
                                        hash.TransformFinalBlock(swap ? buffer : buffer2, 0, read);
                                        copyprogress.Hash = hash.Hash;
                                    }
                                    else
                                        hash.TransformBlock(swap ? buffer : buffer2, 0, read, default(byte[]), default(int));
                                }
                                if (writer != null) await writer.ConfigureAwait(false);
                                writer = dest.WriteAsync(swap ? buffer : buffer2, 0, read);
                                swap = !swap;
                                if (progresscallback != null) copyprogress.BytesCopied += read;
                                if (currentblockcount == breakupbytes / bufferSize) break;
                            }
                            if (writer != null) await writer.ConfigureAwait(false);
                        }

                        if (token.IsCancellationRequested)
                        {
                            try
                            {
                                await DeleteAsync(tmpfile, destinationfolder).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                            }
                            return null;
                        }
                        else
                        {
                            if (targetdelete && currentbreakupfile == breakupfiles)
                                await DeleteAsync(file.Name, destinationfolder).ConfigureAwait(false);

                            if (breakupfiles == 1)
                                await RenameAsync(tmpfile, file.Name, destinationfolder).ConfigureAwait(false);
                            else
                                await RenameAsync(tmpfile, breakupfilename, destinationfolder).ConfigureAwait(false);

                            if (MoveFile && currentbreakupfile == breakupfiles)
                            {
                                list = (await ListAsync(TimeSpan.FromTicks(-1), destinationfolder).ConfigureAwait(false)).ToList();
                                if (list.Exists(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                                    file.Delete();
                                else
                                    throw new Exception($"FTPSession.UploadFileAsync: Target file {file.Name} does not exist after apparently successful transfer - not deleting source file");
                            }

                            if (progresscallback != null)
                            {
                                if (copyprogress.BytesCopied != len) throw new Exception("BytesCopied is not the length of the file");
                                copyprogress.IsCompleted = true;
                                progresscallback(copyprogress);
                            }
                        }
                    }
                }
                return copyprogress;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        public FileCopyProgress UploadFile(FileInfo file, bool overwrite, string destinationfolder = null, bool MoveFile = false, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, int breakupmb = 0, bool processhash = true, double maxmbpersec = 0, bool usetmpfile = true, TimeSpan listupdatethreshold = default)
        {
            FileCopyProgress copyprogress;
            int currentblockcount = 0;
            ManualResetEventSlim limiter = null;
            //int limitermsgs = 0;
            try
            {
                StatusUpdate($"UploadFile: initializing for {file.Name}", 5);
                if (!file.Exists) throw new IOException($"FTPSession.UploadFileAsync: Source file {file.FullName} does not exist");

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize]; //, buffer2 = new byte[bufferSize];
                long len = file.Length;
                copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew(), OperationDuring = "Transferring", OperationComplete = "Transfer" };
                long breakupbytes = breakupmb * 1024 * 1024;
                int breakupfiles = 1;
                if (breakupbytes > 0 && breakupbytes < len)
                    breakupfiles = Convert.ToInt32(Math.Ceiling(len / (double)breakupbytes));

                int msperloop = 0;
                if (maxmbpersec > 0)
                {
                    msperloop = Convert.ToInt32(1000 / (maxmbpersec * 1024 * 1024 / bufferSize)) - 20;
                    if (msperloop <= 0) msperloop = 1;
                    limiter = new ManualResetEventSlim();
                }


                if (destinationfolder == null) destinationfolder = CurrentFolder;
                destinationfolder = NormalizeFolder(destinationfolder);

                bool targetdelete = false;
                StatusUpdate($"UploadFile: Getting List for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                var list = List(listupdatethreshold, destinationfolder).ToList();
                StatusUpdate($"UploadFile: List complete for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                if (list.Exists(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!overwrite)
                        throw new Exception($"FTPSession.UploadFileAsync: Target file {file.Name} exists - overwrite was not specified");
                    else
                        targetdelete = true;
                }

                if (progresscallback != null && progressreportinterval == default)
                    progressreportinterval = TimeSpan.FromSeconds(1);
                decimal lastpercent = 0;

                for (int currentbreakupfile = 1; currentbreakupfile <= breakupfiles; currentbreakupfile++)
                {
                    var breakupfilename = $"{currentbreakupfile:00000}.{breakupfiles:00000}.{file.Name}.breakup";
                    if (breakupfiles > 1) list = List(TimeSpan.FromTicks(-1), destinationfolder).ToList();
                    if (breakupfiles <= 1 || !list.Exists(x => x.FileName.Equals(breakupfilename, StringComparison.OrdinalIgnoreCase)))
                    {
                        string tmpfile;
                        if (usetmpfile)
                            tmpfile = Guid.NewGuid().ToString().Replace("-", "") + ".tmp";
                        else if (breakupfiles > 1)
                            tmpfile = breakupfilename;
                        else
                            tmpfile = file.Name;

                        if (!usetmpfile && targetdelete && currentbreakupfile == breakupfiles)
                            Delete(file.Name, destinationfolder);

                        var request = GetRequest(WebRequestMethods.Ftp.UploadFile, tmpfile, destinationfolder);


                        StatusUpdate($"UploadFile: Establishing connections for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);

                        using (var hash = new SHA1CryptoServiceProvider())
                        using (var source = file.OpenRead())
                        using (var dest = request.GetRequestStream())
                        {
                            //dest.SetLength(source.Length);
                            if (processhash) hash.Initialize();
                            source.Seek((currentbreakupfile - 1) * breakupbytes, 0);
                            int read;
                            long curstart = 0;
                            StatusUpdate($"UploadFile: Entering loop for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                            for (long size = 0; size < len; size += read)
                            {
                                if (msperloop > 0) curstart = copyprogress.Stopwatch.ElapsedMilliseconds;
                                currentblockcount++;
                                if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                                {
                                    last = copyprogress.Stopwatch.Elapsed;
                                    progresscallback(copyprogress);
                                }
                                read = source.Read(buffer, 0, bufferSize);
                                if (processhash)
                                {
                                    if (source.Position == len)
                                    {
                                        hash.TransformFinalBlock(buffer, 0, read);
                                        copyprogress.Hash = hash.Hash;
                                    }
                                    else
                                        hash.TransformBlock(buffer, 0, read, default(byte[]), default(int));
                                }
                                dest.Write(buffer, 0, read);
                                copyprogress.BytesCopied += read;
                                if (copyprogress.BytesCopied == len) StatusUpdate($"UploadFile: bytes equal for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                                if (currentblockcount == breakupbytes / bufferSize) break;
                                if (msperloop > 0)
                                {
                                    if (currentblockcount % 10 == 0)
                                    {
                                        var currate = copyprogress.RateMBPerSec;
                                        var curratio = currate / maxmbpersec;
                                        msperloop = Convert.ToInt32(msperloop * curratio);
                                        StatusUpdate($"UploadFile for {file.Name}: rate has been {currate} - msperloop changed to {msperloop} at block count {currentblockcount} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                                        if (msperloop <= 0) msperloop = 1;
                                    }
                                    limiter.Wait(msperloop);
                                }
                                else if (DebugLevel >= 5 && copyprogress.PercentComplete - lastpercent >= 0.1m)
                                {
                                    StatusUpdate(copyprogress.ToString(), 5);
                                    lastpercent = lastpercent + 0.1m;
                                }
                            }
                        }

                        StatusUpdate($"UploadFile: out of loop for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);

                        if (usetmpfile && targetdelete && currentbreakupfile == breakupfiles)
                            Delete(file.Name, destinationfolder);

                        if (usetmpfile)
                        {
                            if (breakupfiles == 1)
                                Rename(tmpfile, file.Name, destinationfolder);
                            else
                                Rename(tmpfile, breakupfilename, destinationfolder);
                            StatusUpdate($"UploadFile: Rename complete for {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                        }

                        if (MoveFile && currentbreakupfile == breakupfiles)
                        {
                            list = List(TimeSpan.FromTicks(-1), destinationfolder).ToList();
                            if (list.Exists(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                                file.Delete();
                            else
                                throw new Exception($"FTPSession.UploadFileAsync: Target file {file.Name} does not exist after apparently successful transfer - not deleting source file");
                        }
                        if (progresscallback != null)
                        {
                            if (copyprogress.BytesCopied != len) throw new Exception("BytesCopied is not the length of the file");
                            copyprogress.IsCompleted = true;
                            progresscallback(copyprogress);
                        }
                        StatusUpdate($"UploadFile: {copyprogress}", 5);
                        StatusUpdate($"/UploadFile: {file.Name} (elapsed: {copyprogress.Stopwatch.ElapsedMilliseconds})", 5);
                    }
                }
                copyprogress.BytesCopied = len;
                return copyprogress;
            }
            catch (Exception ex)
            {
                StatusUpdate($"FTPSession.UploadFile: Error Occurred for {file.Name}: {ex.Message}", 16);
                throw ex;
            }
        }
        public async Task<FileCopyProgress> DownloadFileAsync(FileInfo file, CancellationToken token, bool overwrite, string remotefolder = null, bool MoveFile = false, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default)
        {
            try
            {
                if (remotefolder == null) remotefolder = CurrentFolder;
                remotefolder = NormalizeFolder(remotefolder);
                var list = await ListAsync(default, remotefolder).ConfigureAwait(false);
                var srcfile = list.Where(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (srcfile == null) throw new IOException($"FTPSession.DownloadFileAsync: Source file {file.Name} does not exist in folder {remotefolder}");

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
                bool swap = false;
                long len = srcfile.Length;
                Task writer = null;

                if (file.Exists && !overwrite) throw new Exception($"FTPSession.DownloadFileAsync: Target file {file.FullName} exists - overwrite was not specified");


                FileCopyProgress copyprogress = null;
                if (progresscallback != null)
                {
                    if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                    copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew(), OperationDuring = "Transferring", OperationComplete = "Transfer" };
                }

                var tmpfile = new FileInfo(Path.Combine(file.DirectoryName, Guid.NewGuid().ToString().Replace("-", "") + ".tmp"));
                var request = GetRequest(WebRequestMethods.Ftp.DownloadFile, file.Name, remotefolder);

                using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                using (var source = response.GetResponseStream())
                using (var dest = tmpfile.OpenWrite())
                {
                    dest.SetLength(len);
                    int read;
                    for (long size = 0; size < len; size += read)
                    {
                        if (token.IsCancellationRequested) break;
                        if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                        {
                            last = copyprogress.Stopwatch.Elapsed;
                            progresscallback(copyprogress);
                        }
                        read = await source.ReadAsync(swap ? buffer : buffer2, 0, bufferSize).ConfigureAwait(false);
                        writer?.Wait();
                        writer = dest.WriteAsync(swap ? buffer : buffer2, 0, read);
                        swap = !swap;
                        if (progresscallback != null) copyprogress.BytesCopied += read;
                    }
                    writer?.Wait();
                }

                if (token.IsCancellationRequested)
                {
                    try
                    {
                        tmpfile.Delete();
                    }
                    catch (Exception)
                    {
                    }
                    return null;
                }
                else
                {
                    if (file.Exists) file.Delete();

                    tmpfile.MoveTo(file.FullName);

                    var waitloop = 0;
                    while (!file.Exists)
                    {
                        waitloop++;
                        await Task.Delay(100).ConfigureAwait(false);
                        file.Refresh();
                        if (waitloop > 10) throw new Exception($"FTPSession.DownloadFileAsync: file {file.Name} - transfer has ostensibly completed, but it did not appear in the destination");
                    }
                    if (MoveFile) await DeleteAsync(file.Name, remotefolder).ConfigureAwait(false);

                    if (progresscallback != null)
                    {
                        copyprogress.BytesCopied = len;
                        progresscallback(copyprogress);
                    }
                    return copyprogress;
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        public FileCopyProgress DownloadFile(FileInfo file, bool overwrite, string remotefolder = null, bool MoveFile = false)
        {
            try
            {
                if (remotefolder == null) remotefolder = CurrentFolder;
                remotefolder = NormalizeFolder(remotefolder);
                var list = List(default, remotefolder);
                var srcfile = list.Where(x => x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (srcfile == null) throw new IOException($"FTPSession.DownloadFileAsync: Source file {file.Name} does not exist in folder {remotefolder}");

                var last = TimeSpan.Zero;

                const int bufferSize = 1024 * 1024;  //1MB
                byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
                bool swap = false;
                long len = srcfile.Length;
                Task writer = null;

                if (file.Exists && !overwrite) throw new Exception($"FTPSession.DownloadFileAsync: Target file {file.FullName} exists - overwrite was not specified");


                FileCopyProgress copyprogress = null;
                copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = file.Name, Stopwatch = Stopwatch.StartNew(), OperationDuring = "Transferring", OperationComplete = "Transfer" };

                var tmpfile = new FileInfo(Path.Combine(file.DirectoryName, Guid.NewGuid().ToString().Replace("-", "") + ".tmp"));
                var request = GetRequest(WebRequestMethods.Ftp.DownloadFile, file.Name, remotefolder);

                using (var response = request.GetResponse())
                using (var source = response.GetResponseStream())
                using (var dest = tmpfile.OpenWrite())
                {
                    dest.SetLength(len);
                    int read;
                    for (long size = 0; size < len; size += read)
                    {
                        read = source.Read(swap ? buffer : buffer2, 0, bufferSize);
                        writer?.Wait();
                        writer = dest.WriteAsync(swap ? buffer : buffer2, 0, read);
                        swap = !swap;
                        copyprogress.BytesCopied += read;
                    }
                    writer?.Wait();
                }

                if (file.Exists) file.Delete();

                tmpfile.MoveTo(file.FullName);

                var waitloop = 0;
                while (!file.Exists)
                {
                    waitloop++;
                    Thread.Sleep(100);
                    file.Refresh();
                    if (waitloop > 10) throw new Exception($"FTPSession.DownloadFileAsync: file {file.Name} - transfer has ostensibly completed, but it did not appear in the destination");
                }
                if (MoveFile) Delete(file.Name, remotefolder);

                copyprogress.BytesCopied = len;
                return copyprogress;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        public virtual void StatusUpdate(string update, int severity)
        {
            if (severity >= DebugLevel)
            {
                //Foundation.Log(update);
                //Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}: {update}");
                Foundation.Log(update);
            }
        }

        public static FileInfo CombineFile(FileInfo file, bool overwrite)
        {
            if (file.Name == "any_breakup_file")
            {
                file = file.Directory.GetFiles("*.breakup").FirstOrDefault();
                if (file != null)
                {
                    var pos = file.Name.Substring(11).IndexOf('.');
                    file = new FileInfo(Path.Combine(file.DirectoryName, file.Name.Substring(12 + pos, file.Name.Length - pos - 20)));
                }
            }

            if (file == null) return null;

            //at this point, "file" must be the name of the final output file - whether it exists or not

            FileInfo outFile = new FileInfo(file.FullName + ".combine");
            FileInfo outctl = new FileInfo(file.FullName + ".cntrl.combine");
            int CopyBufferSize = 1024 * 1024;
            byte[] InBuffer = new byte[CopyBufferSize];
            int BytesRead = 0;


            var anybreakupfile = file.Directory.GetFiles("*" + file.Name + ".breakup").FirstOrDefault();
            //var anybreakupfile = file.Directory.GetFiles("*.breakup").Where(x => x.Name.IndexOf(file.Name,StringComparison.OrdinalIgnoreCase) != -1).FirstOrDefault();'

            //if no breakup files exist, there's nothing to do, but it's not necessarily an error - more might be coming
            if (anybreakupfile == null) return null;

            var pos1 = anybreakupfile.Name.Substring(5).IndexOf('.');
            var pos2 = anybreakupfile.Name.Substring(6 + pos1).IndexOf('.');


            int TotalFiles = Convert.ToInt32(anybreakupfile.Name.Substring(6 + pos1, pos2 - pos1));

            int CurFile;
            if (!outctl.Exists)
                CurFile = 1;
            else
                CurFile = Convert.ToInt32(File.ReadAllText(outctl.FullName));
            while (CurFile <= TotalFiles)
            {
                string InFileName = Path.Combine(file.DirectoryName, $"{CurFile:00000}.{TotalFiles:00000}.{file.Name}.breakup");
                //if (ai == null)
                //    processstatus.SubStep = string.Format("reading file {0} / {1}", CurFile, TotalFiles);
                //else
                //    ai.processstatus.SubStep = string.Format("reading file {0} / {1}", CurFile, TotalFiles);
                using (FileStream fsIn = new FileStream(InFileName, FileMode.Open, FileAccess.Read, FileShare.None, CopyBufferSize))
                {
                    using (FileStream fsOut = new FileStream(outFile.FullName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, CopyBufferSize)) //, FileOptions.WriteThrough))
                    {
                        fsOut.Seek(0, SeekOrigin.End);
                        BytesRead = fsIn.Read(InBuffer, 0, CopyBufferSize);
                        while (BytesRead != 0)
                        {
                            fsOut.Write(InBuffer, 0, BytesRead);
                            BytesRead = fsIn.Read(InBuffer, 0, CopyBufferSize);
                        }
                    }
                }
                File.Delete(InFileName);
                CurFile++;
                File.WriteAllText(outctl.FullName, CurFile.ToString());
            }
            outctl.Delete();
            if (file.Exists)
            {
                if (overwrite)
                    file.Delete();
                else
                    throw new Exception($"target file of combine '{file.FullName}' already exists and overwrite was not specified - combine is complete");
            }
            outFile.MoveTo(file.FullName);
            return file;
        }
    }
    public class FTPFile
    {
        public string FileName { get; set; }
        public long Length { get; set; }
        public DateTime LastModified { get; set; }
        public string SourceLine { get; set; }
        public bool IsDirectory { get; set; }
        public override string ToString()
        {
            if (!IsDirectory)
                return $"{FileName} - {Length} bytes / {LastModified}";
            else
                return $"{FileName} - (dir) / {LastModified}";

        }
        public static FTPFile FromLine(string line)
        {
            var f = new FTPFile();
            f.SourceLine = line;
            DateTime dt;

            if (DateTime.TryParseExact(line.Substring(0, 17), "MM-dd-yy  hh:mmtt", new CultureInfo("en-US"), DateTimeStyles.None, out dt))
            {
                f.LastModified = dt;
                var size = line.Substring(17, 22).Trim();
                if (size != "<DIR>")
                {
                    if (long.TryParse(size, out long fs))
                    {
                        f.Length = fs;
                    }
                    else
                    {
                        Foundation.Log($"FTPFile.FromLine: unable to extract file length from input line:");
                        Foundation.Log(line);
                        Foundation.Log($"/FTPFile.FromLine: unable to extract file length from input line:");
                        throw new Exception($"FTPFile.FromLine: unable to extract file length from input line");

                    }
                }
                else
                {
                    f.IsDirectory = true;
                }

                var fn = line.Substring(39);
                if (!string.IsNullOrWhiteSpace(fn))
                {
                    f.FileName = fn;
                }
                else
                {
                    Foundation.Log($"FTPFile.FromLine: unable to extract file name from input line:");
                    Foundation.Log(line);
                    Foundation.Log($"/FTPFile.FromLine: unable to extract file name from input line:");
                    throw new Exception("FTPFile.FromLine: unable to extract file name from input line");
                }
            }
            else
            {
                var eosize = line.Substring(40).IndexOf(' ') + 1; // finding the space at the end of the size
                var size = line.Substring(32, eosize + 8).Trim();
                if (long.TryParse(size, out long fs))
                {
                    f.Length = fs;
                }
                else
                {
                    Foundation.Log($"FTPFile.FromLine: unable to extract file length from input line:");
                    Foundation.Log(line);
                    Foundation.Log($"/FTPFile.FromLine: unable to extract file length from input line:");
                    throw new Exception("FTPFile.FromLine: unable to extract file length from input line");
                }

                if (Foundation.TryParseDateTime(line.Substring(40 + eosize, 12), out dt))
                {
                    f.LastModified = dt;
                }
                else
                {
                    throw new Exception("FTPFile.FromLine: unable to extract file date from input line");
                }

                var fn = line.Substring(53 + eosize);
                if (!string.IsNullOrWhiteSpace(fn))
                {
                    f.FileName = fn;
                }
                else
                {
                    throw new Exception("FTPFile.FromLine: unable to extract file name from input line");
                }
            }



            return f;
        }
    }
    public class FTPDateFormat : IFormatProvider
    {
        public object GetFormat(Type formatType)
        {
            if (formatType == typeof(DateTimeFormatInfo))
                return this;
            else
                return null;
        }
        public string Format(object value)
        {
            return "foo";
        }
    }
    public class RemoteFileList
    {
        public DateTime LastUpdated { get; set; }
        public List<FTPFile> List { get; set; }
    }
    public class CombineFileInfo
    {
        public FileInfo FinalFile { get; set; }
        public FileInfo CombineFile { get; set; }
        public FileStream FileStream { get; set; }
        public Task WriteTask { get; set; }
    }
}
