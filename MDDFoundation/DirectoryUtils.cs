using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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
            EnumerateDirectory(path, searchPattern, recursive, results, currentProgress, progress, progressreportinterval);
            currentProgress.IsComplete = true;
            progress?.Report(currentProgress);
            return results;
        }

        /// <summary>
        /// This is the async version of DirectoryContents. It uses Task.Run to execute the synchronous method in a separate thread.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="searchPattern"></param>
        /// <param name="recursive"></param>
        /// <returns></returns>
        public static Task<List<FileEntry>> DirectoryContentsAsync(string path, string searchPattern = "*", bool recursive = false, IProgress<DirectoryContentsProgress> progress = null, TimeSpan progressreportinterval = default)
        {
            return Task.Run(() => DirectoryContents(path, searchPattern, recursive, progress, progressreportinterval));
        }

        private static void EnumerateDirectory(string path, string searchPattern, bool recursive, List<FileEntry> results, DirectoryContentsProgress currentProgress, IProgress<DirectoryContentsProgress> progress = null, TimeSpan progressreportinterval = default)
        {
            if (progressreportinterval == default)
            {
                progressreportinterval = TimeSpan.FromSeconds(1); // Default to 1 second if not specified
            }
            var lastReportTick = Environment.TickCount;

            string searchPath = Path.Combine(path, "*");
            WIN32_FIND_DATA findData;

            IntPtr findHandle = FindFirstFile(searchPath, out findData);
            if (findHandle == new IntPtr(-1))
                return;

            try
            {
                do
                {
                    string fileName = findData.cFileName;
                    if (string.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == "..")
                        continue;

                    string fullPath = Path.Combine(path, fileName);

                    bool isDirectory = (findData.dwFileAttributes & 0x10) != 0;
                    if (isDirectory)
                    {
                        currentProgress.DirectoriesProcessed++;
                        currentProgress.CurrentDirectory = fullPath;
                        currentProgress.FilesCurrentDirectory = 0;
                        progress?.Report(currentProgress); // Report whenever directory changes regardless of interval
                        lastReportTick = Environment.TickCount; // Reset report tick
                        if (recursive)
                        {
                            EnumerateDirectory(fullPath, searchPattern, recursive, results, currentProgress, progress, progressreportinterval);
                        }
                    }
                    else
                    {
                        // Only add files matching the searchPattern
                        if (Path.GetFileName(fullPath).ToLower().EndsWith(searchPattern.TrimStart('*').ToLower()))
                        {
                            currentProgress.TotalFilesFound++;
                            currentProgress.FilesCurrentDirectory++;
                            // Compose FileInfo using the full path (metadata is already available)
                            results.Add(FileEntry.FromFindData(fullPath, findData));
                        }
                    }
                    if (Environment.TickCount - lastReportTick >= progressreportinterval.TotalMilliseconds)
                    {
                        // Report progress at specified intervals
                        progress?.Report(currentProgress);
                        lastReportTick = Environment.TickCount; // Reset report tick
                    }
                }
                while (FindNextFile(findHandle, out findData));
            }
            finally
            {
                FindClose(findHandle);
            }
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
    public class FileEntry
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
        public int TotalFilesFound { get; set; }
        public int DirectoriesProcessed { get; set; }
        public int FilesCurrentDirectory { get; set; }
        public string CurrentDirectory { get; set; }
        public bool IsComplete { get; set; } = false;   
        public override string ToString()
        {
            if (IsComplete)
                return $"Total Files Found: {TotalFilesFound}, Directories Processed: {DirectoriesProcessed}";
            return $"Scanning {CurrentDirectory} ({DirectoriesProcessed} Directories Processed)... {FilesCurrentDirectory} files found so far in current directory, {TotalFilesFound} total files found so far";
        }
    }
}