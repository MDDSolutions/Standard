# CLAUDE.md

## What This Project Does

**FileRelay** is a .NET library for reliable, resumable, parallel chunked file transfer over HTTP. It is designed for use within a closed ecosystem of applications (all written by the same developer) where the sender and receiver are both known .NET clients. It is not a general-purpose interoperability protocol.

Core capabilities:
- **Resumable uploads**: the server tracks which chunks it has confirmed; a reconnecting sender asks "what do you need?" and sends only missing chunks
- **Parallel chunk delivery**: multiple chunks in-flight simultaneously across N connections for throughput
- **Multicast on receipt**: a single upload can fan out to multiple destination paths on the server side as chunks arrive
- **Post-transfer extensibility**: pluggable handler invoked when a file is complete, allowing the consuming application to define what happens next (index it, restore it, move it, etc.)
- **Hash verification per chunk**: each chunk is verified on arrival; the server does not acknowledge an unverified chunk
- **Self-contained server state**: the server owns all transfer state; the client only knows what the server tells it during negotiation

## Why Not tus

The tus open protocol was considered and rejected. Reasons:
- tus parallel upload (Concatenation extension) requires creating N separate upload resources and a final merge request — more complex than index-based chunk tracking
- tus is designed for interoperability with unknown clients; this library has no such requirement
- multicast, post-transfer hooks, and domain-specific state backends are all outside tus scope
- for a closed library, clean interfaces beat protocol compliance

## Solution Layout

```
FileRelay.Core        — shared models, interfaces, chunk math, hash utilities
                            No framework dependencies. Referenced by both client and server.

FileRelay.Client      — sender implementation
                            Depends on: Core, System.Net.Http
                            Works in: WinForms, Windows Service, Console, ASP.NET, anything .NET

FileRelay.Server      — receiver implementation
                            Depends on: Core, ASP.NET Core
                            Integrated into any ASP.NET Core app via AddChunkedTransfer() / MapChunkedTransfer()

FileRelay.Storage.Sqlite      — SQLite implementation of ITransferStateStore (embedded, no server required)
FileRelay.Storage.SqlServer   — SQL Server implementation of ITransferStateStore
```

## Core Interfaces (defined in FileRelay.Core)

```csharp
// Where chunk state is persisted. Implementations: SQLite, SQL Server.
public interface ITransferStateStore
{
    Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request);
    Task ConfirmChunkAsync(Guid transferId, int chunkIndex);
    Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId);
    Task MarkCompleteAsync(Guid transferId);
}

// Where assembled files are written. Multiple targets = multicast.
public interface ITransferTarget
{
    Task WriteChunkAsync(Guid transferId, int chunkIndex, Stream data, CancellationToken ct);
    Task AssembleAsync(Guid transferId, CancellationToken ct);
    Task VerifyAsync(Guid transferId, string expectedHash, CancellationToken ct);
}

// Invoked by the server after a transfer is fully assembled and verified.
// The consuming application implements this to define what happens with the file.
public interface ITransferCompleteHandler
{
    Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct);
}

// Hash algorithm used for chunk verification. Default: SHA-256.
public interface IChunkVerifier
{
    string ComputeHash(Stream data);
    bool Verify(Stream data, string expectedHash);
}
```

## Transfer Protocol

All endpoints are mounted under a configurable base path (default: `/transfer`).

### 1. Negotiate
```
POST /transfer/negotiate
Request:  { filename, fileSizeBytes, fileHash, chunkSizeMB }
Response: { transferId, chunkSizeMB, totalChunks, chunksNeeded: [1, 2, 3, ...] }
```
- Server creates or resumes a transfer record
- `chunksNeeded` is the full list of unconfirmed chunk indices
- On first contact this is all chunks; on resume it is only missing chunks
- The sender does not decide chunk size — the server does (returned in response)

### 2. Upload Chunk
```
POST /transfer/{transferId}/chunk/{chunkIndex}
Headers:  X-Chunk-Hash: sha256:<base64>
Body:     raw bytes (the chunk)
Response: 200 OK on success, 409 if already confirmed, 4xx on hash mismatch
```
- Server verifies hash before acknowledging
- Server writes to all configured targets before acknowledging
- Server updates state store before returning 200
- Client only removes a chunk from its send queue when it receives 200

### 3. Status
```
GET /transfer/{transferId}/status
Response: { transferId, filename, chunksTotal, chunksConfirmed, chunksNeeded, isComplete }
```
- Used by the sender to re-negotiate after a reconnect if needed
- Can also be polled by monitoring/UI

### 4. Completion
- Triggered internally by the server when the last chunk is confirmed
- Server assembles the file from chunks (if not streaming-assembled), verifies the whole-file hash, then invokes `ITransferCompleteHandler`
- No explicit client call needed; the final chunk's 200 response may include `{ complete: true }`

## Server Integration

```csharp
// In Program.cs or Startup.cs of any ASP.NET Core application:
builder.Services.AddChunkedTransfer(options =>
{
    options.BasePath = "/transfer";
    options.ChunkSizeMB = 50;
    options.StateStore = new SqliteTransferStateStore("transfers.db");
    options.Targets = new[] { new LocalDirectoryTarget(@"/mnt/nas/backups") };
    options.OnComplete = new MyTransferCompleteHandler();
});

app.MapChunkedTransfer();
```

The library adds routes alongside whatever else the application does. It does not require a dedicated service.

## Client Usage

```csharp
var client = new FileRelayClient(new Uri("https://receiver-host/"));
await client.UploadFileAsync(
    new FileInfo(@"C:\backups\mydb.bak"),
    options: new UploadOptions
    {
        ParallelConnections = 4,
        OnProgress = (progress) => Console.WriteLine(progress),
        CancellationToken = cts.Token
    });
```

- Client calls Negotiate, receives chunk list, uploads missing chunks in parallel
- On any connection failure, client re-calls Negotiate to get updated missing chunk list and resumes
- Client does not persist state — the server is the source of truth

## Key Design Rules

- **Never buffer a whole chunk in server memory.** Stream the request body directly to the target writer(s). Chunks may be 50–200MB.
- **The server is the source of truth for transfer state.** The client is stateless between connections. If the client process restarts, it calls Negotiate again and gets the current missing chunk list.
- **Chunk confirmation is atomic.** The state store must not mark a chunk confirmed until it has been fully written to all targets and hash-verified.
- **The client does not know about targets or multicast.** It sends to one endpoint. What happens on the server side is invisible to the client.
- **The `ITransferCompleteHandler` is fire-and-forget from the protocol's perspective.** If it fails, that is the consuming application's problem to handle — do not fail the transfer or block the final chunk response on it.
- **Chunk indices are 1-based integers.** `totalChunks` is derived from `ceil(fileSizeBytes / chunkSizeBytes)`. The last chunk may be smaller than `chunkSizeMB`.

## Deployment Notes

- The server component runs on any ASP.NET Core host: Windows, Linux, Docker
- Designed to run on a QNAP NAS via Container Station (Docker) or as a self-contained Linux binary
- `FileRelay.Storage.Sqlite` is the preferred state backend for self-contained deployment (no external database dependency)
- `FileRelay.Storage.SqlServer` is available when the host has LAN access to a SQL Server instance
- Configure Kestrel's `MaxRequestBodySize` to at least `chunkSizeMB * 1024 * 1024` — the default (30MB) will reject large chunks with HTTP 413
