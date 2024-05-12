using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MDDFoundation
{
    public static class BreakupFiles
    {
        public static int NumBreakupFiles(long filelength, int breakupmb)
        {
            if (breakupmb == 0) return 0;
            return Convert.ToInt32(Math.Ceiling(filelength / ((double)breakupmb * 1024 * 1024)));
        }
        public static List<FileInfo> FindBreakupFiles(string folder)
        {
            var l = new List<FileInfo>();
            var di = new DirectoryInfo(folder);
            foreach (var file in di.GetFiles("*.breakup"))
            {
                if (BreakupFileName.TryParse(file.Name, out var bf))
                {
                    if (!l.Any(x => x.Name.Equals(bf.TargetFileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        l.Add(new FileInfo(Path.Combine(di.FullName, bf.TargetFileName)));
                    }
                }
            }
            return l;
        }
        public static async Task<FileInfo[]> CombineFileAsync(FileInfo file, CancellationToken token, bool overwrite, FileInfo[] targetfiles = null, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, bool processhash = true)
        {
            if (file.Name == "any_breakup_file")
            {
                file = file.Directory.GetFiles("*.breakup").Where(x => !x.Name.EndsWith(".cntrl.breakup")).FirstOrDefault();
                if (file != null)
                {
                    var pos = file.Name.Substring(11).IndexOf('.');
                    file = new FileInfo(Path.Combine(file.DirectoryName, file.Name.Substring(12 + pos, file.Name.Length - pos - 20)));
                }
            }

            if (file == null) return null;

            //at this point, "file" must be the auto-generated name of the final output file - whether it exists or not - of course targetfile can override that name/location

            if (targetfiles == null) targetfiles = new FileInfo[] { new FileInfo(file.FullName) };

            var outputset = new CombineFileInfo[targetfiles.Length];

            for (int i = 0; i < targetfiles.Length; i++)
            {
                var cur = new CombineFileInfo();
                if (targetfiles[i].Name == "auto_combine_name")
                    cur.FinalFile = new FileInfo(Path.Combine(targetfiles[i].DirectoryName, file.Name));
                else
                    cur.FinalFile = new FileInfo(targetfiles[i].FullName);
                cur.CombineFile = new FileInfo(cur.FinalFile.FullName + ".combine");
                outputset[i] = cur;
            }

            int CopyBufferSize = 1024 * 1024;
            byte[] InBuffer = new byte[CopyBufferSize];
            int BytesRead = 0;


            var breakupfiles = BreakupFileName.ListFromFolder(file); // file.Directory.GetFiles("*" + file.Name + ".breakup");

            //if no breakup files exist, there's nothing to do, but it's not necessarily an error - more might be coming
            if (breakupfiles == null || breakupfiles.Count == 0) return outputset.Select(x => x.CombineFile).ToArray();

            var progress = new FileCopyProgress { OperationDuring = "Combining", OperationComplete = "Combine" };
            if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);


            int TotalFiles = breakupfiles[0].NumBreakupFiles;

            var controlfile = BreakupControlFile.FromFile(new FileInfo(file.FullName + ".cntrl.breakup"));

            using (var hash = new SHA1CryptoServiceProvider())
            {
                while (controlfile.CurFile <= TotalFiles)
                {
                    if (processhash) hash.Initialize();
                    progress.BytesCopied = 0;
                    progress.IsCompleted = false;
                    progress.Cancelled = false;
                    progress.Queued = false;
                    progress.IncompleteButNotError = false;
                    if (token.IsCancellationRequested) return null;
                    var curfile = breakupfiles.FirstOrDefault(x => x.BreakupIndex == controlfile.CurFile);
                    progress.FileName = $"{outputset[0].FinalFile.Name} ({controlfile.CurFile}/{TotalFiles})";
                    if (curfile != null)
                    {
                        progress.FileSizeBytes = curfile.FileInfo.Length;

                        progress.Stopwatch = Stopwatch.StartNew();
                        var last = TimeSpan.Zero;
                        if (controlfile.BreakupSizeMB == 0 && controlfile.CurFile < TotalFiles)
                            controlfile.BreakupSizeMB = Convert.ToInt32(progress.FileSizeBytes / 1024.0 / 1024.0);
                        if (controlfile.BreakupSizeMB == 0)
                            throw new Exception("BreakupSizeMB should not be zero at this point");

                        using (FileStream fsIn = new FileStream(curfile.FileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.None, CopyBufferSize))
                        {
                            var correctstartpos = (Convert.ToInt64(controlfile.CurFile) - 1) * controlfile.BreakupSizeMB * 1024 * 1024;

                            var correctendpos = correctstartpos + fsIn.Length;
                            try
                            {
                                for (int i = 0; i < outputset.Length; i++)
                                {
                                    var fs = new FileStream(outputset[i].CombineFile.FullName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, CopyBufferSize);
                                    fs.Seek(0, SeekOrigin.End);
                                    if (fs.Position != correctstartpos)
                                    {
                                        if (fs.Position > correctstartpos)
                                        {
                                            Foundation.Log($"file {fs.Name} needed to be rewound from position {fs.Position} to {correctstartpos}");
                                            fs.Seek(correctstartpos, SeekOrigin.Begin);
                                        }
                                        else
                                        {
                                            if (controlfile.CurFile > 1 && controlfile.BreakupSizeMB > 0)
                                            {
                                                var msg = $"file {fs.Name} is not as long as expected - it is {fs.Position} bytes long and needs to be at {correctstartpos} in order to resume the combine with CurFile {controlfile.CurFile} - it might be possible to recover this combine by transferring earlier files - this process has reset CurFile back by 1 - it is possible a subsequent run of this process could succeed if the correct files are present";
                                                controlfile.CurFile -= 1;
                                                controlfile.ToFile();
                                                throw new Exception(msg);

                                            }
                                            else
                                            {
                                                throw new Exception($"file {fs.Name} is not as long as expected - it is {fs.Position} bytes long and needs to be at {correctstartpos} in order to resume the combine with CurFile {controlfile.CurFile} - this process was not able to reset CurFile for some reason so action is required");
                                            }
                                        }
                                    }
                                    outputset[i].FileStream = fs;
                                }

                                BytesRead = await fsIn.ReadAsync(InBuffer, 0, CopyBufferSize).ConfigureAwait(true);
                                progress.BytesCopied += BytesRead;
                                while (BytesRead != 0)
                                {
                                    if (processhash) hash.TransformBlock(InBuffer, 0, BytesRead, default, default);
                                    if (progresscallback != null && (progress.Stopwatch.Elapsed - last) > progressreportinterval)
                                    {
                                        last = progress.Stopwatch.Elapsed;
                                        progresscallback(progress);
                                    }
                                    for (int i = 0; i < outputset.Length; i++)
                                    {
                                        outputset[i].WriteTask = outputset[i].FileStream.WriteAsync(InBuffer, 0, BytesRead);
                                    }
                                    await Task.WhenAll(outputset.Select(x => x.WriteTask)).ConfigureAwait(false);

                                    BytesRead = await fsIn.ReadAsync(InBuffer, 0, CopyBufferSize).ConfigureAwait(true);
                                    progress.BytesCopied += BytesRead;
                                }
                                if (processhash)
                                {
                                    hash.TransformFinalBlock(InBuffer, 0, 0);
                                    progress.Hash = hash.Hash;
                                }

                            }
                            catch (Exception)
                            {

                                throw;
                            }
                            finally
                            {
                                for (int i = 0; i < outputset.Length; i++)
                                {
                                    var fs = outputset[i].FileStream;
                                    if (fs != null)
                                    {
                                        if (fs.Position != correctendpos)
                                        {
                                            Foundation.Log($"file {fs.Name} should be at {correctendpos} but is at {fs.Position}");
                                            //may not need to do anything here if rewinding above works
                                        }
                                        fs.Close();
                                        fs.Dispose();
                                    }
                                }
                            }

                        }
                        if (progresscallback != null)
                        {
                            if (progress.BytesCopied != progress.FileSizeBytes)
                            {
                                throw new Exception("BytesCopied is not the length of the file");
                            }
                            else
                            {
                                //this allows a monitoring process to do something when each file completes, but reset it again because the overall process may not be done
                                progress.IsCompleted = true;
                                progresscallback(progress);
                                progress.IsCompleted = false;
                            }
                        }
                        curfile.FileInfo.Delete();
                        controlfile.CurFile++;
                        controlfile.ToFile();
                    }
                    else
                    {
                        progress.IsCompleted = false;
                        progress.IncompleteButNotError = true;
                        progresscallback?.Invoke(progress);
                        return null;
                    }
                }
                if (controlfile.CurFile > TotalFiles)
                {
                    controlfile.FileInfo.Delete();
                    var now = DateTime.Now;
                    for (int i = 0; i < outputset.Length; i++)
                    {
                        if (outputset[i].FinalFile.Exists)
                        {
                            if (overwrite)
                                outputset[i].FinalFile.Delete();
                            else
                                throw new Exception($"target file of combine '{outputset[i].FinalFile.FullName}' already exists and overwrite was not specified - combine is complete");
                        }
                        outputset[i].CombineFile.MoveTo(outputset[i].FinalFile.FullName);
                        File.SetCreationTime(outputset[i].FinalFile.FullName, now);
                        File.SetLastWriteTime(outputset[i].FinalFile.FullName, now);
                        File.SetLastAccessTime(outputset[i].FinalFile.FullName, now);
                    }
                    progress.IsCompleted = true;
                    progresscallback?.Invoke(progress);
                    return outputset.Select(x => x.FinalFile).ToArray();
                }
                else
                {
                    throw new Exception("BreakupFiles.CombineFileAsync: This should never happen but it's not necessarily a huge problem");
                }
            }
        }
        public static async Task<byte[]> CopyFragmentAsync(FileInfo source, FileInfo target, long lengthmb = 0, long offsetsourcemb = 0, long offsettargetmb = 0, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default, bool computehash = true)
        {
            const int bufferSize = 1024 * 1024;  //1MB
            FileCopyProgress copyprogress = null;
            var last = TimeSpan.Zero;
            long len;
            if (lengthmb == 0)
            {
                len = source.Length - (offsetsourcemb * bufferSize);
            }
            else
            {
                len = lengthmb * bufferSize;
            }
            if (len > source.Length - (offsetsourcemb * bufferSize))
            {
                len = source.Length - (offsetsourcemb * bufferSize);
            }

            if (len < 1) throw new ArgumentException("Write length calculated to be less than 1 byte");

            if (progresscallback != null)
            {
                if (progressreportinterval == default) progressreportinterval = TimeSpan.FromSeconds(1);
                copyprogress = new FileCopyProgress { FileSizeBytes = len, FileName = source.Name, Stopwatch = Stopwatch.StartNew() };
            }

            byte[] buffer = new byte[bufferSize], buffer2 = new byte[bufferSize];
            bool swap = false;
            Task writer = null;
            byte[] finalhash = null;

            using (var hash = new SHA1CryptoServiceProvider())
            using (var srcstream = source.OpenRead())
            using (var tgtstream = new FileStream(target.FullName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize))
            {
                if (computehash) hash.Initialize();
                if (offsetsourcemb > 0) srcstream.Seek(offsetsourcemb * bufferSize, SeekOrigin.Begin);
                if (offsettargetmb > 0) tgtstream.Seek(offsettargetmb * bufferSize, SeekOrigin.Begin);
                int read;
                for (long size = 0; size < len; size += read)
                {
                    if (progresscallback != null && (copyprogress.Stopwatch.Elapsed - last) > progressreportinterval)
                    {
                        last = copyprogress.Stopwatch.Elapsed;
                        progresscallback(copyprogress);
                    }
                    read = await srcstream.ReadAsync(swap ? buffer : buffer2, 0, bufferSize).ConfigureAwait(false);
                    if (computehash)
                    {
                        if (size + read == len)
                        {
                            hash.TransformFinalBlock(swap ? buffer : buffer2, 0, read);
                            if (progresscallback != null) copyprogress.Hash = hash.Hash;
                            finalhash = hash.Hash;
                        }
                        else
                            hash.TransformBlock(swap ? buffer : buffer2, 0, read, default(byte[]), default(int));
                    }
                    writer?.Wait();
                    writer = tgtstream.WriteAsync(swap ? buffer : buffer2, 0, read);
                    swap = !swap;
                    if (progresscallback != null) copyprogress.BytesCopied += read;
                }
                writer?.Wait();
            }
            return finalhash;
        }
    }
    public class BreakupFileName
    {
        public BreakupFileName(int breakupindex, int breakupfiles, string filename)
        {
            TargetFileName = filename;
            BreakupIndex = breakupindex;
            NumBreakupFiles = breakupfiles;
        }
        public BreakupFileName(string name)
        {
            if (IsValid(name))
            {
                BreakupIndex = int.Parse(name.Substring(0, 5));
                NumBreakupFiles = int.Parse(name.Substring(6, 5));
                TargetFileName = name.Substring(12, name.Length - 20);
            }
            else
            {
                throw new ArgumentException($"'{name}' is not a valid breakupfile name");
            }
        }
        public string TargetFileName { get; set; }
        public int BreakupIndex { get; set; }
        public int NumBreakupFiles { get; set; }
        public bool IsLastFile { get => BreakupIndex == NumBreakupFiles; }
        public override string ToString()
        {
            if (BreakupIndex == 0) return "<invalid>";
            return $"{BreakupIndex:00000}.{NumBreakupFiles:00000}.{TargetFileName}.breakup";
        }
        public FileInfo FileInfo { get; set; }
        public static bool IsValid(string name)
        {
            return !name.EndsWith(".cntrl.breakup") && name.EndsWith(".breakup") && Regex.IsMatch(name, "^\\d{5}\\.\\d{5}.*");
        }
        public static bool TryParse(string name, out BreakupFileName result)
        {
            if (IsValid(name))
            {
                result = new BreakupFileName(name);
                return true;
            }
            else
            {
                result = null;
                return false;
            }
        }
        public static List<BreakupFileName> ListFromFolder(FileInfo folderfile)
        {
            var l = new List<BreakupFileName>();    
            foreach (var file in folderfile.Directory.GetFiles($"*{folderfile.Name}.breakup"))
            {
                if (TryParse(file.Name, out var breakupFile)) 
                {
                    breakupFile.FileInfo = file;
                    l.Add(breakupFile); 
                }
            }
            return l;
        }
    }
    public class BreakupControlFile
    {
        public int CurFile { get; set; }
        public int BreakupSizeMB { get; set; }
        public FileInfo FileInfo { get; set; }
        public void ToFile()
        {
            if (FileInfo == null) throw new Exception("FileInfo property cannot be null");
            File.WriteAllText(FileInfo.FullName, $"CurFile={CurFile};BreakupSizeMB={BreakupSizeMB};");
        }
        public static BreakupControlFile FromFile(FileInfo file)
        {
            var r = new BreakupControlFile();
            r.FileInfo = file;
            if (file.Exists)
            {
                var controlfiletext = File.ReadAllText(file.FullName);
                if (!int.TryParse(controlfiletext, out var curfile))
                {
                    r.CurFile = int.Parse(Foundation.TextBetween(controlfiletext, "CurFile=", ";"));
                    r.BreakupSizeMB = int.Parse(Foundation.TextBetween(controlfiletext, "BreakupSizeMB=", ";"));
                }
                else
                {
                    r.CurFile = curfile;
                }
            }
            else
            {
                r.CurFile = 1;
            }
            return r;
        }
    }
}
