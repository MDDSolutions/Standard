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
FileRelay.Storage.SqlServer   — SQL Server implementation of ITransferStateStore
```

## Core Interfaces (defined in FileRelay.Core)

```csharp
// Where chunk state is persisted. Implementations: SQLite, SQL Server.
public interface ITransferStateStore
{
    Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB);
    Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash);
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
// CompletedTransfer includes AppId so the handler knows which user completed the transfer.
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

// Persists and rotates per-user API keys. Optional — if not configured, SeedKey is used directly.
// SqliteKeyStore (Storage.Sqlite) is the standard implementation.
public interface IKeyStore
{
    Task SeedAsync(string appId, string seedKey);       // INSERT OR IGNORE on first run
    Task<KeyAuthResult?> AuthenticateAsync(string appId, string providedKey, TimeSpan gracePeriod);
    Task<bool> HasActiveGracePeriodAsync(string appId);
    Task<string> RotateAsync(string appId, byte[] clientEntropy);
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

All endpoints are mounted under a configurable base path (default: `/transfer`). All require `X-App-Id` and `Authorization: Bearer {key}` headers.

### 1. Negotiate
```
POST /transfer/negotiate
Request:  { filename, fileSizeBytes, fileHash, chunkSizeMB }
Response: { transferId, chunkSizeMB, totalChunks, chunksNeeded: [1, 2, 3, ...] }
```
- Server creates or resumes a transfer record **scoped to the authenticating AppId**
- `chunksNeeded` is the full list of unconfirmed chunk indices
- On first contact this is all chunks; on resume it is only missing chunks
- The sender does not decide chunk size — the server does (returned in response)

### 2. Upload Chunk
```
POST /transfer/{transferId}/chunk/{chunkIndex}
Headers:  X-Chunk-Token: <hmac over appId + transferId + chunkIndex + runIndex>
Body:     raw bytes (the chunk) + 32-byte SHA-256 + 32-byte HMAC-SHA256
Response: 200 OK on success, 409 if already confirmed, 4xx on hash mismatch
```
- Server verifies the transfer belongs to the authenticating AppId (returns 404 otherwise)
- Server verifies the streamed chunk hash before acknowledging
- Server verifies the appended hash HMAC before acknowledging, so an HTTP-path attacker cannot replace both the chunk bytes and hash
- Server writes to the user's configured targets before acknowledging
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

### 5. Key Rotation
```
POST /transfer/rotate-key
Request:  { clientEntropy: "<base64 32 bytes>" }
Response: { newKey: "<base64>" }
```
- Requires current key or previous key (only if `GracePeriodEnd` is NULL)
- Blocked if any grace period is currently active
- Returns 501 if no `IKeyStore` is configured on the server

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
    apiKey: storedKey);   // load from persistent storage; updated after each rotation

await client.UploadFileAsync(
    new FileInfo(@"C:\backups\mydb.bak"),
    options: new UploadOptions
    {
        ParallelConnections = 4,
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
- **The `ITransferCompleteHandler` is fire-and-forget from the protocol's perspective.** If it fails, that is the consuming application's problem to handle — do not fail the transfer or block the final chunk response on it.
- **Chunk indices are 1-based integers.** `totalChunks` is derived from `ceil(fileSizeBytes / chunkSizeBytes)`. The last chunk may be smaller than `chunkSizeMB`.
- **Transfer state is scoped per AppId.** A resume lookup matches on AppId + filename + file size + context. Two different users uploading a file with the same name do not collide.
- **Targets are per-user, not global.** Each `AppUser` defines where its files are written. `FileRelayOptions` has no top-level `Targets` property.

## Future Feature: HMAC Per-Chunk Tokens (HTTP Data Path for Constrained TLS Endpoints)

### Problem

Some deployment targets (e.g. old ARM NAS hardware with no hardware AES) cannot terminate TLS at meaningful throughput. The backup data itself may already be encrypted at rest, making TLS confidentiality redundant. However, simply running the data path over plain HTTP with a static API key is unacceptable because the key is long-lived and would be visible in every request.

### Proposed Solution

Split the security responsibilities across the two phases that already exist in the protocol:

1. **Negotiate (HTTPS)** — authenticates the client with the API key over an encrypted channel and establishes the transfer.
2. **Upload chunks (HTTP)** — each chunk request carries its chunk-specific token in a header. The token is unguessable to an attacker watching the HTTP stream because it is derived from the API key, which only traveled over HTTPS.

### Token Design

Tokens are derived using HMAC-SHA256 — no storage required, and no token list needs to be transmitted:

```
token[i] = HMAC-SHA256(key: apiKey, data: appId || transferId || chunkIndex || runIndex)
hashMac[i] = HMAC-SHA256(key: apiKey, data: appId || transferId || chunkIndex || runIndex || length || chunkHash)
```

- Both client and server independently compute the token from inputs they already have: `apiKey` (the current live key from `IKeyStore`), `transferId` (returned by negotiate), `chunkIndex` (known per-chunk)
- The negotiate response does not need to include tokens — nothing changes in the protocol response
- Client includes `X-Chunk-Token: <token>` on each HTTP chunk upload
- Client appends `chunkHash` and `hashMac` to the streamed request body after reading the chunk once
- Server recomputes the expected request token on arrival and rejects mismatches with 401
- Server recomputes the chunk hash while streaming the body, then verifies the appended `hashMac` before confirming the chunk
- The API key is never transmitted over HTTP

### Why This Is Sufficient

An attacker on the HTTP path:
- **Cannot forge a token** — HMAC requires the API key, which only traveled over HTTPS
- **Cannot replay a token usefully** — each token is bound to a chunk and run index, and claimed runs are rejected on duplicate use
- **Cannot corrupt data silently** — changing the bytes requires changing the chunk hash, but the attacker cannot forge the hash HMAC
- **Can read plaintext payload bytes** unless the payload is already encrypted before FileRelay sees it
- **Can cause retries** (denial of service) — this is true of any unencrypted protocol and is acceptable for a backup transfer use case

### Protocol Changes Required

Negotiate response gains one optional field:
```
Response: { transferId, chunkSizeMB, totalChunks, chunksNeeded: [1, 2, 3, ...], httpDataPort: 61488 }
```

- `httpDataPort` is present only when the server has an HTTP data path configured; absent otherwise
- Its presence is the client's signal to switch: upload chunks over HTTP to that port, and include `X-Chunk-Token`
- No client-side flags or modes needed — the response is self-describing

Chunk upload gains one required header and a keyed trailer when uploading over the HTTP data path:
```
Headers: X-Chunk-Token: <hmac>
Body:    raw chunk bytes || sha256(chunk bytes) || hmac(metadata + chunk hash)
```

`FileRelayOptions` would need an `HttpDataPort: int?` property (default null = disabled). When set, the server accepts HTTP chunk uploads on that port provided a valid token is present, and skips the HTTPS enforcement filter for chunk routes only.

### When to Implement

Only worthwhile when TLS termination is genuinely impractical at the server endpoint — e.g. a constrained ARM device with software-only AES where the backup payload is already encrypted. For all other deployments, standard HTTPS for the full session is simpler, equally secure, and preferred.

## Deployment Notes

- The server component runs on any ASP.NET Core host: Windows, Linux, Docker
- Designed to run on a QNAP NAS via Container Station (Docker) or as a self-contained Linux binary
- `FileRelay.Storage.Sqlite` is the preferred state backend for self-contained deployment — `SqliteTransferStateStore` and `SqliteKeyStore` both take the same DB file path and coexist in the same database
- `FileRelay.Storage.SqlServer` is available when the host has LAN access to a SQL Server instance (does not implement `IKeyStore`)
- Configure Kestrel's `MaxRequestBodySize` to at least `chunkSizeMB * 1024 * 1024` — the default (30MB) will reject large chunks with HTTP 413
- The `SeedKey` in appsettings is only used on first run; inspect the `AppKeys` table in the SQLite database to see the live key after rotation
