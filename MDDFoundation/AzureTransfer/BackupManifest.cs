using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace MDDFoundation
{
    [DataContract]
    public class BackupManifest
    {
        [DataMember(Order = 1)] public string FileName { get; set; }
        [DataMember(Order = 2)] public long FileSize { get; set; }
        [DataMember(Order = 3)] public int ChunkSizeBytes { get; set; }
        [DataMember(Order = 4)] public List<ManifestChunk> Chunks { get; set; } = new List<ManifestChunk>();
        [DataMember(Order = 5)] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        [DataMember(Order = 6)] public DateTime? CompletedUtc { get; set; }

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

        public bool IsComplete => CompletedUtc != null && Chunks.TrueForAll(c => c.Completed);
        public int NumChunks => ChunkSizeBytes != 0 ? (int) Math.Ceiling((double) FileSize / ChunkSizeBytes) : 0;
        public static BackupManifest LoadFromStream(Stream stream)
        {
            var serializer = new DataContractSerializer(typeof(BackupManifest));
            return (BackupManifest)serializer.ReadObject(stream);
        }

        public void SaveToStream(Stream stream)
        {
            var serializer = new DataContractSerializer(typeof(BackupManifest));
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
        public static BackupManifest FromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
                return LoadFromStream(ms);
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
