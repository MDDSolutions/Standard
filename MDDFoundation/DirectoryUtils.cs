using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

            EnumerateDirectory(path, "", searchPattern, recursive, fileprogress, CancellationToken.None); //, currentProgress, progress, progressreportinterval);
            currentProgress.IsComplete = true;
            progress?.Report(currentProgress);
            return results;
        }
        public static void DirectoryContents(string path, Action<FileEntry> fileprogress, string searchPattern = "*", bool recursive = false)
        {
            EnumerateDirectory(path, "", searchPattern, recursive, fileprogress, CancellationToken.None);
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

            await Task.Run(() => EnumerateDirectory(path, "", searchPattern, recursive, fileprogress, CancellationToken.None)).ConfigureAwait(false); //, currentProgress, progress, progressreportinterval);
            currentProgress.IsComplete = true;
            progress?.Report(currentProgress);
            return results;
        }
        public static async Task<int> DirectoryContentsAsync(string path, Action<FileEntry> fileprogress, string searchPattern = "*", bool recursive = false, CancellationToken token = default)
        {
            if (token == default) token = CancellationToken.None; // Use default token if not provided
                                                                  //internalcount = 0; // Reset internal count for each new enumeration
            int count = await Task.Run(() => EnumerateDirectory(path, "", searchPattern, recursive, fileprogress, token)); //, currentProgress, progress, progressreportinterval);
            return count;
            //Console.WriteLine($"MDDFoundation.DirectoryContentsAsync internal count: {internalcount}"); // Output the total count of files processed
        }
        //private static int internalcount;
        private static int EnumerateDirectory(string root, string relativepath, string searchPattern, bool recursive, Action<FileEntry> fileProgress, CancellationToken token) //, DirectoryContentsProgress currentProgress, IProgress<DirectoryContentsProgress> progress = null, TimeSpan progressreportinterval = default)
        {
            //if (Debugger.IsAttached) Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: MDDFoundation.EnumerateDirectory running for {path}...");
            string searchPath = Path.Combine(root, relativepath, "*");
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

                    string fullPath = Path.Combine(root, relativepath, fileName);

                    bool isDirectory = (findData.dwFileAttributes & 0x10) != 0;
                    if (isDirectory)
                    {
                        if (recursive)
                        {
                            count = count + EnumerateDirectory(root, Path.Combine(relativepath, fileName), searchPattern, recursive, fileProgress, token); //, currentProgress, progress, progressreportinterval);
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
                            fileProgress(new FileEntry(root, relativepath, fileName, findData));
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
            catch (Exception)
            {
                throw;

                //// Log or handle exceptions as needed
                //if (Debugger.IsAttached) Console.WriteLine($"Error processing directory {Path.Combine(root,relativepath)}: {ex.Message}");
                //return -1; // Indicate an error occurred
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
        public static void AppendUniqueLinesToFileAtomic(string path, List<string> lines, Encoding? encoding = null)
        {

            using var filelock = NASFileLock.Acquire($"{path}.lock", 10, TimeSpan.FromSeconds(30));

            encoding ??= Encoding.UTF8;

            // Normalize by removing all whitespace and lower-casing for case-insensitive comparison.
            static string Normalize(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var c in s)
                {
                    if (!char.IsWhiteSpace(c))
                        sb.Append(char.ToLowerInvariant(c));
                }
                return sb.ToString();
            }

            // Retry/backoff configuration
            const int maxAttempts = 4;
            const int initialDelayMs = 100;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Single exclusive access to the file for read+check+append/replace.
                    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    // Read existing lines (respect BOM if present).
                    using var sr = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true);
                    var existingLines = new List<string>();
                    string? existingLine;
                    while ((existingLine = sr.ReadLine()) != null)
                    {
                        existingLines.Add(existingLine);
                    }

                    // Capture the detected encoding for writing and dispose the reader.
                    var writeEncoding = sr.CurrentEncoding ?? encoding;

                    // Build map of normalized -> first index in existing file (preserve order).
                    var existingMap = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < existingLines.Count; i++)
                    {
                        var n = Normalize(existingLines[i]);
                        if (!existingMap.ContainsKey(n))
                            existingMap[n] = i;
                    }

                    // Determine which incoming lines are new (normalize and de-duplicate within the input).
                    var toAppend = new List<string>(capacity: lines.Count);
                    var batchSet = new HashSet<string>(StringComparer.Ordinal); // normalized lines added in this batch
                    bool anyReplacement = false;

                    foreach (var line in lines)
                    {
                        var norm = Normalize(line ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(norm))
                            continue;

                        // If already present in file or already planned in this batch, skip.
                        if (existingMap.ContainsKey(norm) || batchSet.Contains(norm))
                            continue;

                        // Special-case: incoming line with a leading '-' should update an existing entry without the dash.
                        // e.g. existing "foo" and incoming "-foo" -> replace "foo" with "-foo"
                        var stripped = norm.TrimStart('-');
                        if (!string.Equals(stripped, norm, StringComparison.Ordinal) && existingMap.TryGetValue(stripped, out int idx))
                        {
                            // Replace the existing line in-place within the in-memory list.
                            existingLines[idx] = line ?? string.Empty;
                            // Update mapping: remove old key, add new key pointing to same index
                            existingMap.Remove(stripped);
                            existingMap[norm] = idx;
                            anyReplacement = true;
                            continue;
                        }

                        // Otherwise this is a true new line to append
                        toAppend.Add(line ?? string.Empty);
                        batchSet.Add(norm);
                    }

                    if (toAppend.Count == 0 && !anyReplacement)
                        return; // nothing to do

                    // Build final list: existing lines (with replacements applied) + new appended lines
                    var finalLines = new List<string>(existingLines.Count + toAppend.Count);
                    finalLines.AddRange(existingLines);
                    finalLines.AddRange(toAppend);

                    // Truncate file and write all final lines using the detected encoding (preserves BOM if any).
                    fs.SetLength(0);
                    fs.Seek(0, SeekOrigin.Begin);
                    using var sw = new StreamWriter(fs, writeEncoding, 4096, leaveOpen: true);
                    for (int i = 0; i < finalLines.Count; i++)
                    {
                        sw.Write(finalLines[i]);
                        // write trailing newline for every line (matches previous behavior of appending newline after each entry)
                        sw.Write(sw.NewLine);
                    }
                    sw.Flush();
                    fs.Flush(true); // ensure data is flushed to disk
                    return;
                }
                catch (IOException ioEx)
                {
                    lastEx = ioEx;
                    if (attempt == maxAttempts) break;
                    int delay = initialDelayMs * (1 << (attempt - 1));
                    System.Threading.Thread.Sleep(delay + new Random().Next(0, 50));
                    continue;
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    lastEx = uaEx;
                    if (attempt == maxAttempts) break;
                    int delay = initialDelayMs * (1 << (attempt - 1));
                    System.Threading.Thread.Sleep(delay + new Random().Next(0, 50));
                    continue;
                }
            }

            // If we reach here, all attempts failed — surface a fatal exception.
            throw new IOException($"Failed to append unique lines to '{path}' after {maxAttempts} attempts.", lastEx);
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
        public string FullName => Path.Combine(RootFolder, RelativePath, Name);
        public string Name { get; private set; }
        public string DirectoryName => Path.Combine(RootFolder, RelativePath);
        public string Extension { get; set; }
        public string RootFolder { get; set; }
        public string RelativePath { get; set; }
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
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentNullException(nameof(fullPath), "Full path cannot be null or empty.");

            RootFolder = Path.GetPathRoot(fullPath) ?? string.Empty;
            RelativePath = Path.GetDirectoryName(fullPath)?.Substring(RootFolder.Length).TrimStart(Path.DirectorySeparatorChar) ?? string.Empty;
            Name = Path.GetFileName(fullPath);
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
            if (string.IsNullOrWhiteSpace(fullName))
                throw new ArgumentNullException(nameof(fullName), "Full path cannot be null or empty.");

            Name = Path.GetFileName(fullName);
            RootFolder = Path.GetPathRoot(fullName) ?? string.Empty;
            RelativePath = Path.GetDirectoryName(fullName)?.Substring(RootFolder.Length).TrimStart(Path.DirectorySeparatorChar) ?? string.Empty;
            Extension = Path.GetExtension(fullName);
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


        internal FileEntry(string root, string relativepath, string filename, WIN32_FIND_DATA findData)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentNullException(nameof(root), "Root cannot be null or empty.");
            if (relativepath == null)
                throw new ArgumentNullException(nameof(relativepath), "Relative path cannot be null.");
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentNullException(nameof(filename), "File name cannot be null or empty.");

            // Ensure non-nullable fields are initialized deterministically on the hot path

            RootFolder = root;
            RelativePath = relativepath;
            Name = filename;
            Extension = Path.GetExtension(filename);

            Length = ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;

            // Build 64-bit FILETIME values (cast low DWORD to uint to avoid sign-extension)
            long creationFileTime = ((long)findData.ftCreationTime.dwHighDateTime << 32) | (uint)findData.ftCreationTime.dwLowDateTime;
            long lastWriteFileTime = ((long)findData.ftLastWriteTime.dwHighDateTime << 32) | (uint)findData.ftLastWriteTime.dwLowDateTime;
            long lastAccessFileTime = ((long)findData.ftLastAccessTime.dwHighDateTime << 32) | (uint)findData.ftLastAccessTime.dwLowDateTime;

            // Convert FILETIME -> UTC DateTime once, then compute local times
            CreationTimeUtc = DateTime.FromFileTimeUtc(creationFileTime);
            LastWriteTimeUtc = DateTime.FromFileTimeUtc(lastWriteFileTime);
            LastAccessTimeUtc = DateTime.FromFileTimeUtc(lastAccessFileTime);

            CreationTime = CreationTimeUtc.ToLocalTime();
            LastWriteTime = LastWriteTimeUtc.ToLocalTime();
            LastAccessTime = LastAccessTimeUtc.ToLocalTime();

            Attributes = (FileAttributes)findData.dwFileAttributes;
            Exists = true;
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

        public XmlSchema? GetSchema() => null;

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

            Name = Path.GetFileName(FullName);
            RootFolder = Path.GetPathRoot(FullName) ?? string.Empty;
            RelativePath = Path.GetDirectoryName(FullName)?.Substring(RootFolder.Length).TrimStart(Path.DirectorySeparatorChar) ?? string.Empty;
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
        public string? CurrentDirectory { get; set; } = null;
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