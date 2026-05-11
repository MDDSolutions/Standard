namespace FileRelay.Core.Interfaces;

public enum KeyStatus
{
    Current,               // current key
    PreviousGracePending,  // previous key; new key not yet used, countdown not started
    PreviousGraceActive,   // previous key; new key has been used, countdown running
}

public class KeyAuthResult
{
    public KeyAuthResult(KeyStatus status) => Status = status;
    public KeyStatus Status { get; }
}

public interface IKeyStore
{
    /// <summary>
    /// Inserts the seed key for the given app only if no entry exists yet (first-run bootstrap).
    /// Subsequent calls with the same appId are no-ops.
    /// </summary>
    Task SeedAsync(string appId, string seedKey);

    /// <summary>
    /// Verifies the provided key for the given app. Returns null on failure.
    /// Stamps GracePeriodEnd when the current key is first used after a rotation.
    /// </summary>
    Task<KeyAuthResult?> AuthenticateAsync(string appId, string providedKey, TimeSpan gracePeriod);

    /// <summary>
    /// Returns the current key and the previous key when it is still valid for grace
    /// recovery (pending or active, but not expired) for the given app.
    /// Used by the server to compute and validate per-chunk HMAC tokens without
    /// requiring the client to send the raw API key on chunk requests.
    /// Returns null if the appId is not found.
    /// </summary>
    Task<(string Current, string? Previous)?> GetKeysAsync(string appId);

    /// <summary>
    /// Validates a per-chunk HMAC token against known keys using the provided validator,
    /// then runs the same grace-period lifecycle as AuthenticateAsync (stamp/purge).
    /// Returns (status, macKeys) on success, where macKeys are the key(s) valid for
    /// body-HMAC verification. Returns null on authentication failure.
    /// </summary>
    Task<(KeyAuthResult Status, string[] MacKeys)?> AuthenticateChunkAsync(
        string appId, Func<string, bool> validateToken, TimeSpan gracePeriod);

    /// <summary>
    /// Returns true if a grace period is currently active for the given app
    /// (i.e. GracePeriodEnd is set and has not yet elapsed).
    /// </summary>
    Task<bool> HasActiveGracePeriodAsync(string appId);

    /// <summary>
    /// Rotates to a new key derived from server entropy XOR'd with the client's entropy.
    /// Old key moves to PreviousKey; GracePeriodEnd is cleared.
    /// Returns the new key.
    /// </summary>
    Task<string> RotateAsync(string appId, byte[] clientEntropy);
}
