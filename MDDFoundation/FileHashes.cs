using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace MDDFoundation
{
    public class FileHashes
    {
        public string Source { get; set; }
        public string Folder { get; set; }
        public int BreakupSizeMB { get; set; }
        public int BreakupThreshold { get; set; }
        public List<FileFragmentHash> Hashes { get; set; }
        public void SaveTo(string filename)
        {
            using (Stream stream = File.Create(filename))
            {
                XmlSerializer ser = new XmlSerializer(this.GetType());
                ser.Serialize(stream, this);
            }
        }
        public List<FileHashComparisonResult> CompareTo(FileHashes other)
        {
            var l1singlefile = Hashes.Select(x => x.FileName).Distinct().Count() == 1;
            var l2singlefile = other.Hashes.Select(x => x.FileName).Distinct().Count() == 1;

            var l1indexes = Hashes.Select(x => x.FileIndex);
            var l2indexes = other.Hashes.Select(x => x.FileIndex);

            var l1distinctindexes = l1indexes.Distinct().Count() == Hashes.Count();
            var l2distinctindexes = l2indexes.Distinct().Count() == Hashes.Count();

            if (l1singlefile && l2singlefile && l1distinctindexes && l2distinctindexes)
            {
                var l1contiguousstrict = l1indexes.Min() == 1 && l1indexes.Max() == l1indexes.Count();
                var l2contiguousstrict = l2indexes.Min() == 1 && l2indexes.Max() == l2indexes.Count();

                return Hashes.Join(
                        other.Hashes,
                        l1 => new { l1.BreakupFileIndex },
                        l2 => new { l2.BreakupFileIndex },
                        (l1, l2) => new FileHashComparisonResult { FileHash1 = l1, FileHash2 = l2 }).ToList();

            }
            else
            {
                return Hashes.Join(
                    other.Hashes,
                    l1 => new { l1.BreakupFileCombineName, l1.BreakupFileIndex },
                    l2 => new { l2.BreakupFileCombineName, l2.BreakupFileIndex },
                    (l1, l2) => new FileHashComparisonResult { FileHash1 = l1, FileHash2 = l2 }).ToList();
            }

        }

        public async Task AddFile(FileInfo fi, int breakupsize, int breakupthreshold)
        {
            if (fi.Length > breakupthreshold * 1024 * 1024)
            {
                var breakupfiles = BreakupFiles.NumBreakupFiles(fi.Length, breakupsize);
                for (int i = 1; i <= breakupfiles; i++)
                {
                    ProgressUpdate?.Invoke(this, $"Computing hash for {fi.Name} ({i}/{breakupfiles})");
                    var fh = new FileFragmentHash { BreakupSizeMB = breakupsize, FileIndex = i, FileName = fi.Name, Parent = this };
                    await fh.Compute().ConfigureAwait(false);
                    //var hash = await Foundation.ReadFileHashFragmentAsync(fi, i, breakupsize, CancellationToken.None, (x) => ProgressUpdate?.Invoke(this,x.ToString())).ConfigureAwait(false);
                    Hashes.Add(fh);
                }
            }
            else
            {
                ProgressUpdate?.Invoke(this, $"Computing hash for {fi.Name}");
                var fh = new FileFragmentHash { BreakupSizeMB = 0, FileIndex = 0, FileName = fi.Name, Parent = this };
                await fh.Compute().ConfigureAwait(false);
                //var hash = await Foundation.ReadFileHashAsync(fi, CancellationToken.None, (x) => ProgressUpdate?.Invoke(this, x.ToString())).ConfigureAwait(false);
                Hashes.Add(fh);
            }
        }



        public static event EventHandler<string> ProgressUpdate;
        public static void UpdateProgress(FileHashes filehashes, string update)
        {
            ProgressUpdate?.Invoke(filehashes, update);
        }
        public static FileHashes FromXMLFile(string filename)
        {
            var fi = new FileInfo(filename);
            FileHashes r = null;

            if (fi.Exists)
            {
                using (Stream stream = fi.OpenRead())
                {
                    try
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(FileHashes));
                        r = (FileHashes)ser.Deserialize(stream);
                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                }
            }
            if (r != null && r.Hashes != null) r.Hashes.ForEach(x => x.Parent = r);
            return r;
        }
        public async static Task<FileHashes> FromFolderOrFile(string path, int breakupsize, int breakupthreshold)
        {
            if (string.IsNullOrWhiteSpace(path)) { return null; }

            var filehashes = new FileHashes
            {
                Source = path,
                BreakupSizeMB = breakupsize,
                BreakupThreshold = breakupthreshold,
                Hashes = new List<FileFragmentHash>()
            };


            if (path.Contains('*'))
            {
                var di = new DirectoryInfo(Path.GetDirectoryName(path));
                filehashes.Folder = di.FullName;
                foreach (var fi in di.GetFiles(Path.GetFileName(path)))
                {
                    await filehashes.AddFile(fi, breakupsize, breakupthreshold).ConfigureAwait(false);
                }
            }
            else
            {
                var pathasfile = new FileInfo(path);

                if (pathasfile.Exists)
                {
                    filehashes.Folder = pathasfile.DirectoryName;
                    await filehashes.AddFile(pathasfile, breakupsize, breakupthreshold).ConfigureAwait(false);
                }
                else if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    filehashes.Folder = di.FullName;
                    foreach (var fi in di.GetFiles())
                    {
                        await filehashes.AddFile(fi, breakupsize, breakupthreshold).ConfigureAwait(false);
                    }
                }
                else
                {
                    throw new Exception("Couldn't figure out what you're looking for");
                }
            }
            return filehashes;
        }    
    }
    public class FileFragmentHash
    {
        private string filename = null;
        public string FileName {
            get => filename;
            set 
            {
                if (filename != value)
                {
                    BreakupFileName.TryParse(value, out breakupfilename);
                }
                filename = value;
            }
        }
        public int FileIndex { get; set; }
        public int BreakupSizeMB { get; set; }
        public byte[] Hash { get; set; }

        [XmlIgnore]
        public FileHashes Parent { get; set; }
        public string HashHex { get => Hash == null ? "<null>" : BitConverter.ToString(Hash).Replace("-",""); }
        public string Hashb64 { get => Hash == null ? "<null>" : Convert.ToBase64String(Hash); }
        private BreakupFileName breakupfilename = null;
        public int BreakupFileIndex { get => breakupfilename == null ? FileIndex : breakupfilename.BreakupIndex; }
        public string BreakupFileCombineName { get => breakupfilename == null ? filename : breakupfilename.TargetFileName; }
        public override string ToString()
        {
            if (FileIndex == 0)
                return $"{FileName}: {HashHex}";
            return $"{FileName} ({FileIndex}): {HashHex}";
        }
        public string ShortString()
        {
            if (FileIndex == 0)
                return $"{FileName}";
            return $"{FileName} ({FileIndex})";
        }

        public async Task Compute()
        {
            var fi = new FileInfo(Path.Combine(Parent.Folder, FileName));
            Hash = await Foundation.ReadFileHashFragmentAsync(fi, FileIndex, BreakupSizeMB, CancellationToken.None, (x) => FileHashes.UpdateProgress(Parent, x.ToString())).ConfigureAwait(false);
        }
    }
    public class FileHashComparisonResult
    {
        public FileFragmentHash FileHash1 { get; set; }
        public string File1 { get => FileHash1?.ShortString(); }
        public string File1Hash { get => FileHash1.HashHex; }
        public FileFragmentHash FileHash2 { get; set; }
        public string File2 { get => FileHash2?.ShortString(); }
        public string File2Hash { get => FileHash2.HashHex; }
        public bool? IsEqual
        {
            get
            {
                if (FileHash1 == null || FileHash2 == null || FileHash1.Hash == null || FileHash2.Hash == null)
                    return null;
                return FileHash1.Hash.SequenceEqual(FileHash2.Hash);
            }
        }
        public override string ToString()
        {
            return $"File1: {FileHash1.ShortString()} File2: {FileHash2.ShortString()} IsEqual: {IsEqual}";
        }
    }
}
