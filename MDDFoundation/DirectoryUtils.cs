using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MDDFoundation
{
    public static partial class Foundation
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        public static List<FileEntry> DirectoryContents(string path, string searchPattern = "*", bool recursive = false, IProgress<DirectoryContentsProgress> progress = null, TimeSpan progressreportinterval = default)
        {
            var results = new List<FileEntry>();
            var currentProgress = new DirectoryContentsProgress
            {
                CurrentDirectory = path,
                TotalFilesFound = 0,
                DirectoriesProcessed = 0,
                FilesCurrentDirectory = 0
            };
            var lastReportTick = Environment.TickCount;

            var fileprogress = new Action<FileEntry>(file =>
            {
                results.Add(file);
                if (progress != null)
                {
                    if (file.DirectoryName != currentProgress.CurrentDirectory)
                    {
                        currentProgress.DirectoriesProcessed++;
                        currentProgress.CurrentDirectory = file.DirectoryName;
                        currentProgress.FilesCurrentDirectory = 1;
                        progress.Report(currentProgress);
                        lastReportTick = Environment.TickCount; // Reset report tick
                    }
                    else
                    {
                        currentProgress.FilesCurrentDirectory++;
                    }
                    currentProgress.TotalFilesFound++;
                    // Report progress at specified intervals
                    if (progressreportinterval != default && Environment.TickCount - lastReportTick >= progressreportinterval.TotalMilliseconds)
                    {
                        progress.Report(currentProgress);
                        lastReportTick = Environment.TickCount; // Reset report tick
                    }
                }
            });

            EnumerateDirectory(path, searchPattern, recursive, fileprogress, CancellationToken.None); //, currentProgress, progress, progressreportinterval);
            currentProgress.IsComplete = true;
            progress?.Report(currentProgress);
            return results;
        }
        public static void DirectoryContents(string path, Action<FileEntry> fileprogress, string searchPattern = "*", bool recursive = false)
        {
            EnumerateDirectory(path, searchPattern, recursive, fileprogress, CancellationToken.None);
        }
        public static async Task <List<FileEntry>> DirectoryContentsAsync(string path, string searchPattern = "*", bool recursive = false, IProgress<DirectoryContentsProgress> progress = null, TimeSpan progressreportinterval = default)
        {
            var results = new List<FileEntry>();
            var currentProgress = new DirectoryContentsProgress
            {
                CurrentDirectory = path,
                TotalFilesFound = 0,
                DirectoriesProcessed = 0,
                FilesCurrentDirectory = 0
            };
            var lastReportTick = Environment.TickCount;

            var fileprogress = new Action<FileEntry>(file =>
            {
                results.Add(file);
                if (file.DirectoryName != currentProgress.CurrentDirectory)
                {
                    currentProgress.DirectoriesProcessed++;
                    currentProgress.CurrentDirectory = file.DirectoryName;
                    currentProgress.FilesCurrentDirectory = 1;
                    progress?.Report(currentProgress);
                    lastReportTick = Environment.TickCount; // Reset report tick
                }
                else
                {
                    currentProgress.FilesCurrentDirectory++;
                }
                currentProgress.TotalFilesFound++;
                // Report progress at specified intervals
                if (progressreportinterval != default && Environment.TickCount - lastReportTick >= progressreportinterval.TotalMilliseconds)
                {
                    progress?.Report(currentProgress);
                    lastReportTick = Environment.TickCount; // Reset report tick
                }
            });

            await Task.Run(() => EnumerateDirectory(path, searchPattern, recursive, fileprogress, CancellationToken.None)).ConfigureAwait(false); //, currentProgress, progress, progressreportinterval);
            currentProgress.IsComplete = true;
            progress?.Report(currentProgress);
            return results;
        }
        public static async Task<int> DirectoryContentsAsync(string path, Action<FileEntry> fileprogress, string searchPattern = "*", bool recursive = false, CancellationToken token = default)
        {
            if (token == default) token = CancellationToken.None; // Use default token if not provided
                                                                  //internalcount = 0; // Reset internal count for each new enumeration
            int count = await Task.Run(() => EnumerateDirectory(path, searchPattern, recursive, fileprogress, token)); //, currentProgress, progress, progressreportinterval);
            return count;
            //Console.WriteLine($"MDDFoundation.DirectoryContentsAsync internal count: {internalcount}"); // Output the total count of files processed
        }
        //private static int internalcount;
        private static int EnumerateDirectory(string path, string searchPattern, bool recursive, Action<FileEntry> fileProgress, CancellationToken token) //, DirectoryContentsProgress currentProgress, IProgress<DirectoryContentsProgress> progress = null, TimeSpan progressreportinterval = default)
        {
            Console.WriteLine($"MDDFoundation.EnumerateDirectory running for {path}...");
            string searchPath = Path.Combine(path, "*");
            WIN32_FIND_DATA findData;

            IntPtr findHandle = FindFirstFile(searchPath, out findData);
            if (findHandle == new IntPtr(-1))
                return 0;


            var count = 0; // Count of files found in this directory
            try
            {
                do
                {
                    token.ThrowIfCancellationRequested(); // Check for cancellation

                    string fileName = findData.cFileName;
                    if (string.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == "..")
                        continue;

                    string fullPath = Path.Combine(path, fileName);

                    bool isDirectory = (findData.dwFileAttributes & 0x10) != 0;
                    if (isDirectory)
                    {
                        if (recursive)
                        {
                            count = count + EnumerateDirectory(fullPath, searchPattern, recursive, fileProgress, token); //, currentProgress, progress, progressreportinterval);
                        }
                    }
                    else
                    {
                        // Only add files matching the searchPattern
                        if (Path.GetFileName(fullPath).ToLower().EndsWith(searchPattern.TrimStart('*').ToLower()))
                        {
                            //currentProgress.TotalFilesFound++;
                            //currentProgress.FilesCurrentDirectory++;
                            // Compose FileInfo using the full path (metadata is already available)
                            count++;
                            fileProgress(FileEntry.FromFindData(fullPath, findData));
                        }
                    }
                }
                while (FindNextFile(findHandle, out findData));
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation if needed
                return -1;
            }
            catch (Exception ex)
            {
                // Log or handle exceptions as needed
                Console.WriteLine($"Error processing directory {path}: {ex.Message}");
                return -1; // Indicate an error occurred
            }
            finally
            {
                FindClose(findHandle);
            }
            return count;
        }
        public static string SizeDisplay(long sizeInBytes)
        {
            if (sizeInBytes < 1024)
                return $"{sizeInBytes} bytes";
            else if (sizeInBytes < 1024 * 1024)
                return $"{sizeInBytes / 1024.0:F2} KB";
            else if (sizeInBytes < 1024 * 1024 * 1024)
                return $"{sizeInBytes / (1024.0 * 1024):F2} MB";
            else if (sizeInBytes < 1024L * 1024 * 1024 * 1024)
                return $"{sizeInBytes / (1024.0 * 1024 * 1024):F2} GB";
            else if (sizeInBytes < 1024L * 1024 * 1024 * 1024 * 1024)
                return $"{sizeInBytes / (1024.0 * 1024 * 1024 * 1024):F2} TB";
            else
                return $"{sizeInBytes / (1024.0 * 1024 * 1024 * 1024 * 1024):F2} PB"; // Petabytes
        }
    }

    /// <summary>
    /// Represents a file entry, providing detailed information about the file's attributes, timestamps, and existence.
    /// </summary>
    /// <remarks>This class is similar to <see cref="System.IO.FileInfo"/> and provides properties to access
    /// metadata about a file. Instances of <see cref="FileEntry"/> can be created using the public constructor with a
    /// file path or through internal methods. Use the <see cref="Refresh"/> method to update the file's metadata if the
    /// file has changed.  It's main purpose is for processing large numbers of files with metadata - when lists of
    /// FileEntry are created with <see cref="Foundation.DirectoryContents(string, string, bool)"/> then the file system does not have to be accessed
    /// for every file as with FileInfo in order to retrieve the metadata</remarks>
    public class FileEntry : IXmlSerializable
    {
        public string FullName { get; private set; }
        public string Name { get; private set; }
        public string DirectoryName { get; private set; }
        public long Length { get; private set; }
        public DateTime CreationTime { get; private set; }
        public DateTime CreationTimeUtc { get; private set; }
        public DateTime LastWriteTime { get; private set; }
        public DateTime LastWriteTimeUtc { get; private set; }
        public DateTime LastAccessTime { get; private set; }
        public DateTime LastAccessTimeUtc { get; private set; }
        public FileAttributes Attributes { get; private set; }
        public bool Exists { get; private set; }

        // Public constructor (like FileInfo)
        public FileEntry(string fullPath)
        {
            FullName = fullPath;
            Name = Path.GetFileName(fullPath);
            DirectoryName = Path.GetDirectoryName(fullPath);
            Refresh();
        }

        //specialized constructor for creating a FileEntry when all metadata is already known
        public FileEntry(
            string fullName, 
            long length,
            DateTimeOffset creationTime,
            DateTimeOffset lastWriteTime,
            DateTimeOffset lastAccessTime,
            FileAttributes attributes, 
            bool exists)
        {
            FullName = fullName;
            Name = Path.GetFileName(fullName);
            DirectoryName = Path.GetDirectoryName(fullName);
            Length = length;
            CreationTime = creationTime.LocalDateTime;
            CreationTimeUtc = creationTime.UtcDateTime;
            LastWriteTime = lastWriteTime.LocalDateTime;
            LastWriteTimeUtc = lastWriteTime.UtcDateTime;
            LastAccessTime = lastAccessTime.LocalDateTime;
            LastAccessTimeUtc = lastAccessTime.UtcDateTime;
            Attributes = attributes;
            Exists = exists;
        }

        // Internal constructor
        internal FileEntry() { }

        internal static FileEntry FromFindData(string fullPath, WIN32_FIND_DATA findData)
        {
            long length = ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;
            DateTime creationTimeUtc = DateTime.FromFileTimeUtc(
                ((long)findData.ftCreationTime.dwHighDateTime << 32) | (uint)findData.ftCreationTime.dwLowDateTime);
            DateTime lastWriteTimeUtc = DateTime.FromFileTimeUtc(
                ((long)findData.ftLastWriteTime.dwHighDateTime << 32) | (uint)findData.ftLastWriteTime.dwLowDateTime);
            DateTime lastAccessTimeUtc = DateTime.FromFileTimeUtc(
                ((long)findData.ftLastAccessTime.dwHighDateTime << 32) | (uint)findData.ftLastAccessTime.dwLowDateTime);

            return new FileEntry
            {
                FullName = fullPath,
                Name = Path.GetFileName(fullPath),
                DirectoryName = Path.GetDirectoryName(fullPath),
                Length = length,
                CreationTimeUtc = creationTimeUtc,
                CreationTime = creationTimeUtc.ToLocalTime(),
                LastWriteTimeUtc = lastWriteTimeUtc,
                LastWriteTime = lastWriteTimeUtc.ToLocalTime(),
                LastAccessTimeUtc = lastAccessTimeUtc,
                LastAccessTime = lastAccessTimeUtc.ToLocalTime(),
                Attributes = (FileAttributes)findData.dwFileAttributes,
                Exists = true
            };
        }

        // Refresh method (like FileInfo.Refresh)
        public void Refresh()
        {
            var fileInfo = new FileInfo(FullName);
            Exists = fileInfo.Exists;
            if (Exists)
            {
                Length = fileInfo.Length;
                CreationTime = fileInfo.CreationTime;
                CreationTimeUtc = fileInfo.CreationTimeUtc;
                LastWriteTime = fileInfo.LastWriteTime;
                LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                LastAccessTime = fileInfo.LastAccessTime;
                LastAccessTimeUtc = fileInfo.LastAccessTimeUtc;
                Attributes = fileInfo.Attributes;
            }
        }

        public override string ToString() => FullName;

        #region IXmlSerializable Implementation

        public XmlSchema GetSchema() => null;

        public void WriteXml(XmlWriter writer)
        {
            writer.WriteElementString(nameof(FullName), FullName);
            writer.WriteElementString(nameof(Length), Length.ToString());
            writer.WriteElementString(nameof(CreationTime), CreationTime.ToString("o"));
            writer.WriteElementString(nameof(LastWriteTime), LastWriteTime.ToString("o"));
            writer.WriteElementString(nameof(LastAccessTime), LastAccessTime.ToString("o"));
            writer.WriteElementString(nameof(Attributes), ((int)Attributes).ToString());
            writer.WriteElementString(nameof(Exists), Exists.ToString().ToLower());
        }

        public void ReadXml(XmlReader reader)
        {
            reader.MoveToContent();
            reader.ReadStartElement();

            FullName = reader.ReadElementContentAsString(nameof(FullName), "");
            Name = Path.GetFileName(FullName);
            DirectoryName = Path.GetDirectoryName(FullName);
            Length = reader.ReadElementContentAsLong(nameof(Length), "");

            var cto = DateTimeOffset.Parse(reader.ReadElementContentAsString(nameof(CreationTime), ""));
            CreationTime = cto.LocalDateTime;
            CreationTimeUtc = cto.UtcDateTime;

            var lwt = DateTimeOffset.Parse(reader.ReadElementContentAsString(nameof(LastWriteTime), ""));
            LastWriteTime = lwt.LocalDateTime;
            LastWriteTimeUtc = lwt.UtcDateTime;

            var lat = DateTimeOffset.Parse(reader.ReadElementContentAsString(nameof(LastAccessTime), ""));
            LastAccessTime = lat.LocalDateTime;
            LastAccessTimeUtc = lat.UtcDateTime;

            Attributes = (FileAttributes)reader.ReadElementContentAsInt(nameof(Attributes), "");
            Exists = bool.Parse(reader.ReadElementContentAsString(nameof(Exists), ""));

            reader.ReadEndElement();
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
    public class DirectoryContentsProgress
    {
        public int TotalFilesFound { get; set; } = 0;
        public int DirectoriesProcessed { get; set; } = 0;
        public int FilesCurrentDirectory { get; set; } = 0;
        public string CurrentDirectory { get; set; } = null;
        public bool IsComplete { get; set; } = false;
        public bool IsCanceled { get; set; } = false;
        public override string ToString()
        {
            if (IsComplete)
                return $"Total Files Found: {TotalFilesFound}, Directories Processed: {DirectoriesProcessed}";
            if (IsCanceled)
                return $"Process was cancelled. Total Files Found: {TotalFilesFound}, Directories Processed: {DirectoriesProcessed}";
            if (string.IsNullOrEmpty(CurrentDirectory))
                return $"Scan initializing...";
            if (TotalFilesFound == 0 && DirectoriesProcessed == 0)
                return $"Scanning {CurrentDirectory}...";
            return $"Scanning {CurrentDirectory} ({DirectoriesProcessed} Directories Processed)... {FilesCurrentDirectory} files found so far in current directory, {TotalFilesFound} total files found so far";
        }
    }
}