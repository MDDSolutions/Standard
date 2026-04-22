# AzureTransfer Design and Usage Guide

This document expands on the design decisions behind the `AzureTransfer` helper and illustrates how to use it for large file transfers with Azure Blob Storage.

## Design Goals

* **Chunked transfers for very large payloads** – avoid the memory and timeout constraints of single-request uploads by streaming manageable chunks (default 64 MiB) via regular blob PUT operations.
* **Concurrent producer/consumer coordination** – allow a downloader to begin fetching chunks while the uploader is still in progress, without downloading incomplete blobs.
* **Integrity verification** – guarantee that the reassembled file matches the source by computing a SHA-256 hash during upload and verifying it during download.
* **REST-based, dependency-light implementation** – operate on top of `HttpClient` and Azure’s Blob REST API rather than higher-level SDKs so the utility can integrate into the existing MDDFoundation stack with minimal new dependencies.

## Key Techniques and Trade-offs

### Manifest-Based Coordination vs. Blob Renames

Azure Blob Storage does not support atomic renames for block blobs the way FTP can swap temporary files. Instead of relying on renames, the transfer workflow maintains a lightweight **status manifest** (`{baseName}.status`) with the following metadata:

| Field | Purpose |
|-------|---------|
| `FileName` | Human-readable reference to the original file. |
| `FileSize` | Total byte size of the source file. |
| `ChunkSize` | Chunk payload size to validate downloads. |
| `TotalChunks` | Allows the downloader to know when it has all parts. |
| `CompletedChunks` | Number of fully uploaded chunks; drives consumer polling. |
| `Hash` | Hex-encoded SHA-256 hash for integrity validation. |

Uploads update the manifest after each chunk commit, incrementing `CompletedChunks`. Downloaders poll the manifest until the next chunk index is marked complete. This ensures consumers never attempt to read partial blobs, providing the same safety guarantees as the FTP rename approach while using Azure-compatible primitives.

### SHA-256 Placement

Rather than append the hash to the payload (which would require trimming during assembly), the solution stores the SHA-256 digest in the manifest. This keeps each chunk a direct byte-for-byte copy of the original data segments and avoids extra parsing logic or metadata mutation on every chunk.

### REST Calls vs. Azure SDK Blocks

The helper uses `HttpClient` to issue raw REST requests so it can:

* Avoid introducing additional SDK dependencies into the project.
* Maintain explicit control over headers (`x-ms-blob-type`) and payload sizes.
* Remain compatible with environments limited to .NET Standard 2.0 / C# 7.3.

Using the high-level `BlockBlobClient` would simplify chunk management but requires newer Azure SDK packages and would complicate deployment in constrained environments. The current approach offers a predictable, dependency-light path at the cost of a bit more manual HTTP plumbing.

## Upload Workflow

1. Initialize a `TransferStatus` manifest with chunk sizing and total counts.
2. Stream the source file chunk by chunk:
   * Read up to `chunkSizeBytes` into a buffer.
   * Issue an HTTP PUT for `{baseName}.chunkXXXXXX` (zero-padded index).
   * Update `CompletedChunks` in the manifest.
   * Feed the data into a running SHA-256 hash.
3. After all chunks upload, finalize the hash and write it to the manifest.

Because the manifest is persisted after each chunk, downloaders can monitor `CompletedChunks` and fetch chunks immediately after they finish uploading.

## Download Workflow

1. Poll for the manifest, waiting until it appears (upload may still be initializing).
2. Create a temporary `*.partial` file to assemble the output atomically.
3. For each chunk index:
   * Poll the manifest until `CompletedChunks` exceeds the index.
   * Download the chunk blob, validating it does not exceed `ChunkSize`.
   * Append the chunk to the temporary file while updating a SHA-256 hash locally.
4. Wait until the manifest exposes the final `Hash`, then compare it with the computed value.
5. Atomically move the temporary file into place, replacing any previous copy.

This mirrors the FTP approach of keeping incomplete work hidden (via the `.partial` suffix) until it is verified.

## Usage Example

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MDDFoundation;

public class AzureTransferSample
{
    public async Task RunAsync()
    {
        // Example SAS-enabled container URI pieces
        var containerUrl = "https://myaccount.blob.core.windows.net/my-container";
        var sasToken = "sv=2023-01-01&ss=bfqt&srt=sco&sp=rl&se=2024-12-31T23:59:59Z&sig=...";

        var sourceFile = new FileInfo(@"C:\\large\\payload.bin");
        var baseBlobName = "payload"; // AzureTransfer will produce payload.chunk000000, etc.

        using (var transfer = new AzureTransfer(containerUrl, sasToken))
        {
            // Upload in 64 MiB chunks (default). Cancellation is optional.
            await transfer.UploadFileInChunksAsync(sourceFile, baseBlobName, chunkSizeMb: 64, token: CancellationToken.None);

            // Later (possibly on a different machine/process), download and reassemble.
            var destinationPath = Path.Combine(Path.GetTempPath(), sourceFile.Name);
            await transfer.DownloadAndAssembleAsync(baseBlobName, destinationPath, pollInterval: TimeSpan.FromSeconds(2));
        }
    }
}
```

### Customizing Chunk Size and Poll Intervals

* **Chunk size** – `UploadFileInChunksAsync` accepts `chunkSizeMb`; increase it for throughput or reduce for memory-constrained environments.
* **Download polling** – adjust `pollInterval` in `DownloadAndAssembleAsync` to tune responsiveness vs. control-plane cost. A shorter interval reduces latency at the expense of more manifest reads.

## Error Handling Considerations

* `UploadFileInChunksAsync` and `DownloadAndAssembleAsync` honor the provided `CancellationToken`, allowing callers to abort long-running operations cleanly.
* HTTP failures throw via `EnsureSuccessStatusCode`, enabling callers to catch and retry as desired.
* Chunk overruns and hash mismatches throw `InvalidDataException`, signaling corruption or mis-coordination that requires operator attention.

## Cleaning Up

Chunks and the status manifest remain in the container after successful downloads. Depending on retention requirements, callers can issue separate delete requests once all consumers have finished.

