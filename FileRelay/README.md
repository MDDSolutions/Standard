# FileRelay

## What This Project Does

**FileRelay** is a .NET library for reliable, resumable, parallel chunked file transfer over HTTP. It is designed for use within a closed ecosystem of applications (all written by the same developer) where the sender and receiver are both known .NET clients. It is not a general-purpose interoperability protocol.

Core capabilities:
- **Resumable uploads**: the server tracks which chunks it has confirmed; a reconnecting sender asks "what do you need?" and sends only missing chunks
- **Parallel chunk delivery**: multiple chunks in-flight simultaneously across N connections for throughput
- **Multicast on receipt**: a single upload can fan out to multiple destination paths on the server side as chunks arrive
- **Post-transfer extensibility**: pluggable handler invoked when a file is complete, allowing the consuming application to define what happens next (index it, restore it, move it, etc.)
- **Hash verification per chunk**: each chunk is verified on arrival; the server does not acknowledge an unverified chunk
- **Self-contained server state**: the server owns all transfer state; the client only knows what the server tells it during negotiation
- **Per-user isolation**: each registered app (user) has its own API key and target paths; transfers are scoped to the authenticating app
- **Key rotation**: clients can request a new API key at any time; the server issues it and maintains a grace period so in-flight requests with the old key are not immediately rejected

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
                            Integrated into any ASP.NET Core app via AddFileRelay() / MapFileRelay()

FileRelay.Storage.Sqlite      — SQLite implementations of ITransferStateStore and IKeyStore
                                    (embedded, no server required; both use the same DB file)
FileRelay.Storage.SqlServer   — SQL Server implementations of ITransferStateStore and IKeyStore
```

## Core Interfaces (defined in FileRelay.Core)

```csharp
// Where chunk state is persisted. Implementations: SQLite, SQL Server.
public interface ITransferStateStore
{
    Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB);
    Task<TransferState?> GetAsync(Guid transferId);
    Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash);
    Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId);

    // Atomically claims runIndex for (transferId, chunkIndex). Returns false if another
    // request already owns this run — prevents duplicate and replayed chunk uploads.
    Task<bool> TryClaimChunkAsync(Guid transferId, int chunkIndex, int runIndex);

    // Returns the highest claimed run index per chunk. Used by Negotiate to tell a resuming
    // client which run index to use next for any chunk that was claimed but not confirmed.
    Task<IReadOnlyDictionary<int, int>> GetClaimedRunIndexesAsync(Guid transferId);

    // Returns true if this call flipped IsComplete; false if another finalizer got there first.
    Task<bool> MarkCompleteAsync(Guid transferId);

    Task PruneCompletedAsync(TimeSpan retention);
    Task<IReadOnlyList<TransferState>> GetInactiveIncompleteTransfersAsync(TimeSpan inactivityThreshold);
    Task DeleteTransferStateAsync(Guid transferId);
}

// Where uploaded files are written. Multiple targets = multicast.
public interface ITransferTarget
{
    // Pre-allocates the .partial file at the correct size and registers the transfer path.
    Task InitializeAsync(Guid transferId, string filename, long fileSizeBytes, TransferContext? context, CancellationToken ct);

    // Returns a write stream positioned at the correct byte offset for this chunk.
    Task<Stream> OpenChunkWriterAsync(Guid transferId, int chunkIndex, long offset, CancellationToken ct);

    // Verifies the assembled file against expectedHash ("sha256:base64"). Throws on mismatch.
    Task VerifyAsync(Guid transferId, string expectedHash, CancellationToken ct);

    // Renames .partial to final filename.
    Task FinalizeAsync(Guid transferId, CancellationToken ct);

    Task AbortAsync(Guid transferId, CancellationToken ct);

    // Returns false (and deletes a wrong-size partial) if the partial file is missing or corrupt.
    Task<bool> IsPartialIntactAsync(Guid transferId, string filename, long expectedSizeBytes, TransferContext? context, CancellationToken ct);
}

// Invoked by the server after a transfer is finalized on disk.
// The consuming application implements this to define what happens with the completed file.
// CompletedTransfer includes AppId so the handler knows which user completed the transfer.
public interface ITransferCompleteHandler
{
    // Called before the 200 is sent. Return false → 422 to the client. Throw → 500.
    // Use for synchronous validation (virus scan, schema check) the client should know about.
    Task<bool> OnValidateAsync(CompletedTransfer transfer, CancellationToken ct);

    // Called after the 200 is sent. Errors are logged and swallowed — the client is already gone.
    // Use for notifications, indexing, or other work that does not need to block the client.
    Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct);
}

// Persists and rotates per-user API keys. Optional — if not configured, SeedKey is used directly.
// SqliteKeyStore (Storage.Sqlite) is the standard implementation.
public interface IKeyStore
{
    Task SeedAsync(string appId, string seedKey);       // INSERT OR IGNORE on first run
    Task<KeyAuthResult?> AuthenticateAsync(string appId, string providedKey, TimeSpan gracePeriod);
    Task<bool> HasActiveGracePeriodAsync(string appId);
    Task<string> RotateAsync(string appId, byte[] clientEntropy);

    // Validates a per-chunk HMAC token (via lambda) and runs the same grace-period lifecycle
    // as AuthenticateAsync in a single transaction. Returns (status, macKeys) on success —
    // macKeys contains the key(s) valid for body-HMAC verification (both keys during grace).
    Task<(KeyAuthResult Status, string[] MacKeys)?> AuthenticateChunkAsync(
        string appId, Func<string, bool> validateToken, TimeSpan gracePeriod);

    // Returns current and previous key. Primarily used for internal state inspection.
    Task<(string Current, string? Previous)?> GetKeysAsync(string appId);
}
```

## Security Model

### Authentication

Every request requires two headers:

```
X-App-Id: {appId}
Authorization: Bearer {apiKey}
```

The server looks up the registered `AppUser` by `AppId` and verifies the key. If no match is found, or the key is wrong, all endpoints return 401. The `AppId` also determines which target paths files are written to.

Chunk upload requests are the exception — they authenticate via a per-chunk HMAC token instead of the Bearer key (see [Chunk Authentication](#chunk-authentication) below).

A user with no `SeedKey` is valid (open, no key required) — but at least one user must always be configured, since users define where files go.

### AppUser Configuration

```csharp
public class AppUser
{
    public string AppId { get; set; }       // identifier sent in X-App-Id header
    public string SeedKey { get; set; }     // initial key; ignored once IKeyStore has an entry for this AppId
    public IReadOnlyList<ITransferTarget> Targets { get; set; }  // where this user's files are written
}
```

`SeedKey` is only used to bootstrap the key store on first run (`INSERT OR IGNORE`). Once a key store entry exists for an `AppId`, the live key in the store is authoritative and `SeedKey` is ignored. Rename `SeedKey` in config to signal this intent.

### Chunk Authentication

Chunk upload requests authenticate via a per-chunk HMAC token in the `X-Chunk-Token` header. The API key is never transmitted on chunk requests.

```
X-Chunk-Token = HMAC-SHA256(apiKey, appId || transferId || chunkIndex || runIndex)
```

Both client and server independently derive this token from inputs they already have. The server validates the token before reading the request body.

The chunk body also carries an integrity trailer bound to the key:

```
Body: [raw chunk bytes] [sha256(chunk bytes)] [HMAC-SHA256(apiKey, appId || transferId || chunkIndex || runIndex || length || chunkHash)]
```

The trailing HMAC (`hashMac`) is present when API keys are configured. It ensures that an attacker on a plaintext path cannot silently replace chunk data — forging valid data requires the API key, which only ever travels over HTTPS. See [HTTP Data Path](#http-data-path) for the deployment scenario where this matters.

### Key Rotation

Clients request a new key at any time (policy — e.g. every N transfers — is entirely the client's choice):

```
POST /transfer/rotate-key
Request:  { clientEntropy: "<base64 32 bytes>" }
Response: { newKey: "<base64>" }
```

The server generates the new key as `base64(SHA256(serverRandom32 XOR SHA256(clientEntropy)))` — neither side alone controls the output. The key store is updated atomically: `PreviousKey ← CurrentKey`, `CurrentKey ← newKey`, `GracePeriodEnd ← NULL`.

**Grace period**: when the new key is first used, `GracePeriodEnd` is stamped (`now + KeyGracePeriod`, default 1 hour). Until that time elapses, the previous key is still accepted. This covers the dropped-response case: if the client never received the rotate response, it still has the old key, which works because no new key has been used yet (`GracePeriodEnd` is NULL) — the client can issue another rotate with the old key to recover.

**Key status values** (returned as `KeyStatus` enum; surfaced to clients via `X-Key-Status` response header when the previous key was used):
- `Current` — authenticated with the current key, no grace period concerns
- `PreviousGracePending` — previous key accepted; new key not yet used (`X-Key-Status: previous-grace-pending`)
- `PreviousGraceActive` — previous key accepted; countdown running (`X-Key-Status: previous-grace-active`)

**Rotation is blocked** when any grace period is active (either key). The only exception is `PreviousGracePending` (new key not yet used), which allows re-rotation to recover from a dropped response.

**SQLite schema** (`AppKeys` table — same DB file as transfer state):
```
AppId          TEXT  NOT NULL PRIMARY KEY
CurrentKey     TEXT  NOT NULL
PreviousKey    TEXT
GracePeriodEnd TEXT  -- NULL until new key first used after rotation
```

## Transfer Protocol

All endpoints are mounted under a configurable base path (default: `/transfer`). All require `X-App-Id`. Chunk endpoints use `X-Chunk-Token`; all other endpoints use `Authorization: Bearer {key}`.

### 1. Negotiate
```
POST /transfer/negotiate
Request:  { filename, fileSizeBytes, fileHash?, chunkSizeMB, context? }
Response: { transferId, chunkSizeMB, totalChunks, chunksNeeded: [1, 2, 3, ...],
            chunkRunIndexes?: { "3": 2, "7": 3, ... }, httpChunkPort?: 61488 }
```
- Server creates or resumes a transfer record **scoped to the authenticating AppId**
- `chunksNeeded` is the full list of unconfirmed chunk indices; all chunks on first contact, only missing chunks on resume
- The sender does not decide chunk size — the server does (returned in response)
- `chunkRunIndexes` is a sparse map of chunk index → next expected run index, present only when one or more chunks were previously attempted but not confirmed. Chunks absent from this map use run index 1.
- `httpChunkPort` is present when the server has the HTTP data path enabled; its presence is the client's signal to route chunk data over plain HTTP to that port (see [HTTP Data Path](#http-data-path))

### 2. Upload Chunk
```
POST /transfer/{transferId}/chunk/{chunkIndex}
Headers:  X-Chunk-Token: <hmac>
          X-Run-Index: <n>        (omitted when n=1)
Body:     [chunk bytes] [32-byte SHA-256] [32-byte hashMac]  (hashMac present when keys configured)
Response: 200 { isComplete: bool } on success
          409 if chunk already confirmed or run index conflict
          4xx on hash or HMAC mismatch
```
- Server validates `X-Chunk-Token` before reading the body
- Server atomically claims the run index before opening writers — duplicate or replayed uploads are rejected with 409
- Server streams the body directly to all configured targets; the chunk is never fully buffered in memory
- Server verifies SHA-256 and hashMac before confirming the chunk
- Client only removes a chunk from its send queue on 200

### 3. Status
```
GET /transfer/{transferId}/status
Response: { transferId, filename, chunksTotal, chunksConfirmed, chunksNeeded, isComplete }
```
- Used by the sender to re-negotiate after a reconnect if needed
- Can also be polled by monitoring/UI

### 4. Completion
- Triggered internally by the server when the last chunk is confirmed
- Chunks are written directly to byte offsets in a pre-allocated `.partial` file as they arrive; no post-transfer assembly step
- After the last chunk is confirmed, the server verifies the whole-file hash (if provided in negotiate), calls `OnValidateAsync`, renames the `.partial` file, then fires `OnCompleteAsync` after sending 200
- The final chunk's 200 response includes `{ isComplete: true }`

### 5. Key Rotation
```
POST /transfer/rotate-key
Request:  { clientEntropy: "<base64 32 bytes>" }
Response: { newKey: "<base64>" }
```
- Requires current key or previous key (only if `GracePeriodEnd` is NULL)
- Blocked if any grace period is currently active
- Returns 501 if no `IKeyStore` is configured on the server

## HTTP Data Path

Some deployment targets (e.g. ARM NAS hardware with software-only AES) cannot terminate TLS at meaningful throughput. When the payload is already encrypted before FileRelay sees it, TLS confidentiality is redundant — but running the data path over plain HTTP with a static API key is unacceptable.

FileRelay solves this by splitting security responsibilities across the two protocol phases:

1. **Negotiate (HTTPS)** — authenticates the client with the API key over an encrypted channel and establishes the transfer. The negotiate response includes `httpChunkPort` when the server has an HTTP listener configured.
2. **Upload chunks (HTTP)** — each request authenticates via `X-Chunk-Token`, which is derived from the API key and therefore unguessable to an observer on the HTTP path. The chunk body trailer (`hashMac`) ensures data integrity — an attacker cannot replace the bytes and forge a valid MAC without knowing the API key.

### Security properties on the HTTP path

- **Cannot forge a token** — HMAC requires the API key, which only traveled over HTTPS
- **Cannot replay a token usefully** — each token is bound to a specific chunk index and run index; claimed runs are rejected on duplicate use
- **Cannot corrupt data silently** — replacing bytes requires recomputing the chunk hash, but forging the `hashMac` requires the API key
- **Can read plaintext payload bytes** — this is an accepted trade-off; only use the HTTP data path when the payload is already encrypted before FileRelay sees it
- **Can cause retries** (denial of service) — acceptable for a backup transfer use case; the protocol recovers cleanly

### Configuration

```csharp
options.RequireHttps    = true;   // control plane always HTTPS
options.AllowHttpChunks = true;   // chunk data path may use HTTP
options.HttpPort        = 61488;  // advertised to clients in negotiate response
```

When `AllowHttpChunks` is enabled, the server:
- Skips HTTPS enforcement for chunk routes only
- Advertises `HttpPort` as `httpChunkPort` in negotiate responses
- Continues to require HTTPS for negotiate, rotate-key, status, and ping

The client detects `httpChunkPort` in the negotiate response and automatically creates a separate plain-HTTP connection for chunk data. No client-side configuration is required. Set `UploadOptions.UseHttpDataPath = false` to opt out even when the server advertises an HTTP port.

## Server Integration

```csharp
// In Program.cs or Startup.cs of any ASP.NET Core application:
var dbPath = Path.Combine(AppContext.BaseDirectory, "filerelay.db");

builder.Services.AddFileRelay(options =>
{
    options.BasePath     = "/transfer";
    options.ChunkSizeMB  = 50;
    options.RequireHttps = true;
    options.KeyGracePeriod = TimeSpan.FromHours(1);

    options.Users = new[]
    {
        new AppUser
        {
            AppId   = "backup-client",
            SeedKey = "initial-secret-from-config",
            Targets = new[] { new LocalDirectoryTarget(@"/mnt/nas/backups/backup-client") }
        }
    };

    options.StateStore = new SqliteTransferStateStore(dbPath);
    options.KeyStore   = new SqliteKeyStore(dbPath);   // same DB file, separate table
    options.OnComplete = new MyTransferCompleteHandler();
});

app.MapFileRelay();
```

The library adds routes alongside whatever else the application does. It does not require a dedicated service.

On startup, `MapFileRelay()` seeds the key store: for each user, if no `AppKeys` entry exists yet, `SeedKey` is inserted as the initial `CurrentKey`. On subsequent restarts the seed is ignored.

## Client Usage

```csharp
var client = new FileRelayClient(
    new Uri("https://receiver-host/"),
    appId:  "backup-client",
    apiKey: storedKey)   // load from persistent storage; updated after each rotation
{
    ParallelConnections = 4,   // parallel chunk connections (default 4)
    ThrottleMBps        = 0,   // bandwidth cap in MB/s; 0 = unlimited
};

await client.UploadFileAsync(
    new FileInfo(@"C:\backups\mydb.bak"),
    options: new UploadOptions
    {
        OnProgress  = progress => Console.WriteLine(progress),
        OnKeyWarning = status =>
        {
            // "previous-grace-pending"  — didn't receive last rotate response; re-rotate now
            // "previous-grace-active"   — should have rotated already; rotate soon
            Console.WriteLine($"Key warning: {status}");
        }
    });

// Rotate the key (e.g. every N transfers). Caller must persist the returned key.
var newKey = await client.RotateKeyAsync();
SaveKeyToStorage(newKey);
```

- Client calls Negotiate, receives chunk list, uploads missing chunks in parallel
- On any connection failure, client re-calls Negotiate to get updated missing chunk list and resumes
- Client does not persist transfer state — the server is the source of truth
- Client **does** persist the API key — it must survive process restarts and be updated after rotation

## Key Design Rules

- **Never buffer a whole chunk in server memory.** Stream the request body directly to the target writer(s). Chunks may be 50–200MB.
- **The server is the source of truth for transfer state.** The client is stateless between connections. If the client process restarts, it calls Negotiate again and gets the current missing chunk list.
- **Chunk confirmation is atomic.** The state store must not mark a chunk confirmed until it has been fully written to all targets and hash-verified.
- **The client does not know about targets or multicast.** It sends to one endpoint. What happens on the server side is invisible to the client.
- **`OnValidateAsync` blocks the final chunk response.** Use it for synchronous validation the client needs to know about (rejected file → 422). `OnCompleteAsync` is fire-and-forget after the 200 is sent — errors are logged and do not affect the client.
- **Chunk indices are 1-based integers.** `totalChunks` is derived from `ceil(fileSizeBytes / chunkSizeBytes)`. The last chunk may be smaller than `chunkSizeMB`.
- **Transfer state is scoped per AppId.** A resume lookup matches on AppId + filename + file size + context. Two different users uploading a file with the same name do not collide.
- **Targets are per-user, not global.** Each `AppUser` defines where its files are written. `FileRelayOptions` has no top-level `Targets` property.

## Deployment Notes

- The server component runs on any ASP.NET Core host: Windows, Linux, Docker
- Designed to run on a QNAP NAS via Container Station (Docker) or as a self-contained Linux binary
- `FileRelay.Storage.Sqlite` is the preferred state backend for self-contained deployment — `SqliteTransferStateStore` and `SqliteKeyStore` both take the same DB file path and coexist in the same database
- `FileRelay.Storage.SqlServer` is available when the host has LAN access to a SQL Server instance; implements both `ITransferStateStore` and `IKeyStore`
- Configure Kestrel limits via `FileRelayOptions.ConfigureKestrelLimits()` — the defaults (30MB body limit, 128KB HTTP/2 window) will cap throughput and reject large chunks
- The `SeedKey` in appsettings is only used on first run; inspect the `AppKeys` table in the SQLite database to see the live key after rotation
