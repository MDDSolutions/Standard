using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Xml;

namespace MDDFoundation
{
    [DataContract]
    public class TransferManifest
    {
        [DataMember(Order = 1)] public string FileName { get; set; }
        [DataMember(Order = 2)] public long FileSize { get; set; }
        [DataMember(Order = 3)] public int ChunkSizeBytes { get; set; }
        [DataMember(Order = 4)] public List<ManifestChunk> Chunks { get; set; } = new List<ManifestChunk>();
        [DataMember(Order = 5)] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        [DataMember(Order = 6)] public DateTime? CompletedUtc { get; set; }
        [DataMember(Order = 7)] public bool IsSimple { get; set; } = false;
        [DataMember(Order = 8)] public string RelativePath { get; set; }

        public bool IsComplete => CompletedUtc != null && Chunks.TrueForAll(c => c.Completed);
        public int NumChunks => ChunkSizeBytes != 0 ? (int)Math.Ceiling((double)FileSize / ChunkSizeBytes) : 0;
        public ManifestChunk AddChunk(int index, string name, string hash, long size, bool completed)
        {
            var chunk = new ManifestChunk
            {
                Index = index,
                BlobName = name,
                Hash = hash,
                SizeBytes = size,
                UploadedUtc = DateTime.UtcNow,
                Completed = completed
            };
            Chunks.Add(chunk);
            return chunk;
        }
        public void SaveToStream(Stream stream)
        {
            var serializer = new DataContractSerializer(typeof(TransferManifest));
            serializer.WriteObject(stream, this);
        }
        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            {
                SaveToStream(ms);
                return ms.ToArray();
            }
        }
        public override string ToString()
        {
            return $"{FileName} {Chunks.Count} chunks, IsComplete: {IsComplete}";
        }
        public string ManifestURL(string baseurl, string sas)
        {
            if (string.IsNullOrWhiteSpace(RelativePath))
                return $"{baseurl}/{FileName}.manifest?{sas}";
            else
                return $"{baseurl}/{RelativePath}/{FileName}.manifest?{sas}";
        }

        public static TransferManifest LoadFromStream(Stream stream)
        {
            var serializer = new DataContractSerializer(typeof(TransferManifest));
            return (TransferManifest)serializer.ReadObject(stream);
        }
        public static TransferManifest FromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
                return LoadFromStream(ms);
        }
        public static bool LooksLikeChunkName(string blobName) => Regex.IsMatch(blobName, @"\.chunk\d{5}$", RegexOptions.CultureInvariant);
        public static bool LooksLikeManifestName(string blobName) => blobName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
        public static TransferManifest CreateSimpleManifest(string fileName, string relativepath, long size, int chunkSizeBytes)
        {
            var m = new TransferManifest
            {
                FileName = fileName,
                RelativePath = relativepath,
                FileSize = size,
                ChunkSizeBytes = chunkSizeBytes > 0 ? chunkSizeBytes : (int)size, // single chunk
                IsSimple = true
            };
            // One “virtual” chunk that points straight to the blob name.
            m.AddChunk(
                index: 0,
                name: fileName,          // important: not fileName.chunk00000
                hash: null,               // unknown until downloaded (we’ll verify only if present)
                size: size,
                completed: true              // blob already exists in Azure
            );
            // We do NOT set CompletedUtc; the “whole-file complete” flag isn’t needed for single blobs.
            return m;
        }
    }

    [DataContract]
    public class ManifestChunk
    {
        [DataMember(Order = 1)] public int Index { get; set; }
        [DataMember(Order = 2)] public string BlobName { get; set; }
        [DataMember(Order = 3)] public string Hash { get; set; }
        [DataMember(Order = 4)] public long SizeBytes { get; set; }
        [DataMember(Order = 5)] public bool Completed { get; set; }
        [DataMember(Order = 6)] public DateTime UploadedUtc { get; set; }
        public override string ToString()
        {
            return $"{BlobName} Complete: {Completed}";
        }
    }
}
