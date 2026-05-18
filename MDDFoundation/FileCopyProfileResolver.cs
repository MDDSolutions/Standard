using System;
using System.IO;
using System.Linq;

namespace MDDFoundation
{
    /// <summary>
    /// Resolves and applies a <see cref="FileCopyProfile"/> for a copy operation.
    ///
    /// Tweak the profile tables in <see cref="Apply"/> to retune the four built-in profiles.
    /// Memory peak per copy ≈ (2 * PipelineBufferCount + ParallelChunks + 2) * ChunkSizeBytes.
    /// </summary>
    internal static class FileCopyProfileResolver
    {
        // -------------------------------------------------------------------------------------
        // Profile tables — tweak these to retune.
        //
        //   Profile           Buffer    Chunk     Parallel  Pipeline    Peak chunks   Peak MiB
        //   ---------------   -------   -------   --------  --------    -----------   --------
        //   LocalCopy           1 MiB    16 MiB         1         1               5         80
        //   Download            4 MiB    32 MiB         1         2               5        160
        //   Upload              1 MiB    32 MiB         4         2              10        320
        //   RemoteToRemote      4 MiB    32 MiB         4         2              10        320
        //
        // BufferSize  : per-Read() size from source. Big helps SMB; small fine for local.
        // ChunkSize   : verification + pipeline element size.
        // Parallel    : number of write workers. Helps multicast and slow destinations.
        // Pipeline    : capacity of hashQueue and workQueue (each).
        // -------------------------------------------------------------------------------------

        public static void Apply(FileCopyProfile profile, FileCopyProgress p)
        {
            switch (profile)
            {
                case FileCopyProfile.LocalCopy:
                    // Local -> local. Disk-to-disk on the same machine. No need for queue
                    // depth or write parallelism — the bottleneck is the slowest drive.
                    p.BufferSize          = 1  * 1024 * 1024;
                    p.ChunkSizeBytes      = 16L * 1024 * 1024;
                    p.ParallelChunks      = 1;
                    p.PipelineBufferCount = 1;
                    break;

                case FileCopyProfile.Download:
                    // Remote source, local destination(s). SMB rewards larger reads (kernel
                    // read-ahead works better). One reader saturates a single SMB session, so
                    // parallel writers add no value when destinations are local SSD.
                    p.BufferSize          = 4  * 1024 * 1024;
                    p.ChunkSizeBytes      = 32L * 1024 * 1024;
                    p.ParallelChunks      = 1;
                    p.PipelineBufferCount = 2;
                    break;

                case FileCopyProfile.Upload:
                    // Local source, remote destination(s). Local reads are cheap; the bottleneck
                    // is destination SMB writes. Parallel workers help absorb slow targets in
                    // multicast and use multichannel for single-target.
                    p.BufferSize          = 1  * 1024 * 1024;
                    p.ChunkSizeBytes      = 32L * 1024 * 1024;
                    p.ParallelChunks      = 4;
                    p.PipelineBufferCount = 1; //originally 2 - 1 uses less memory and performance is similar
                    break;

                case FileCopyProfile.RemoteToRemote:
                    // Remote source, remote destination(s). Big reads (SMB-friendly) plus
                    // parallel writes.
                    p.BufferSize          = 4  * 1024 * 1024;
                    p.ChunkSizeBytes      = 32L * 1024 * 1024;
                    p.ParallelChunks      = 4;
                    p.PipelineBufferCount = 2;
                    break;

                // Auto and Manual should never reach Apply — they are handled by Resolve.
            }
        }

        // -------------------------------------------------------------------------------------
        // Resolution
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Maps the caller-supplied <paramref name="profile"/> to one of the four concrete
        /// profiles, or returns <see cref="FileCopyProfile.Manual"/> unchanged. Auto inspects
        /// the source and destination paths.
        /// </summary>
        public static FileCopyProfile Resolve(FileCopyProfile profile, FileInfo source, FileInfo[] destinations)
        {
            if (profile != FileCopyProfile.Auto) return profile;

            var sourceRemote = IsRemote(source.FullName);
            var anyDestRemote = destinations.Any(d => d != null && IsRemote(d.FullName));

            return (sourceRemote, anyDestRemote) switch
            {
                (false, false) => FileCopyProfile.LocalCopy,
                (true,  false) => FileCopyProfile.Download,
                (false, true)  => FileCopyProfile.Upload,
                (true,  true)  => FileCopyProfile.RemoteToRemote,
            };
        }

        // -------------------------------------------------------------------------------------
        // Path classification
        // -------------------------------------------------------------------------------------

        private static bool IsRemote(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // UNC paths (\\server\share\...) are always remote.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            // Mapped drives can be remote too. Probing DriveInfo is the most reliable way.
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }
    }
}
