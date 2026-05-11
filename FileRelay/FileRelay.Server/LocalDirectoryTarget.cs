using System.Collections.Concurrent;
using System.Security.Cryptography;
using FileRelay.Core;
using FileRelay.Core.Interfaces;

namespace FileRelay.Server;

public class LocalDirectoryTarget : ITransferTarget
{
    private readonly string _rootPath;
    private readonly ConcurrentDictionary<Guid, (string RelativePath, string Filename)> _transfers = new();

    public LocalDirectoryTarget(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public Task InitializeAsync(Guid transferId, string filename, long fileSizeBytes, TransferContext? context, CancellationToken ct)
    {
        TransferPathValidator.ThrowIfInvalid(filename, context);

        var relativePath = context?.RelativePath ?? "";
        _transfers[transferId] = (relativePath, filename);

        var dir = TransferPathValidator.ResolveDirectory(_rootPath, relativePath);
        Directory.CreateDirectory(dir);

        var partialPath = PartialPath(transferId);
        if (!File.Exists(partialPath))
        {
            using var fs = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.SetLength(fileSizeBytes);
        }

        return Task.CompletedTask;
    }

    public Task<Stream> OpenChunkWriterAsync(Guid transferId, int chunkIndex, long offset, CancellationToken ct)
    {
        var fs = new FileStream(PartialPath(transferId), FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(fs);
    }

    public async Task VerifyAsync(Guid transferId, string expectedHash, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(PartialPath(transferId), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        var hashBytes = await sha.ComputeHashAsync(fs, ct);
        var actual = $"sha256:{Convert.ToBase64String(hashBytes)}";
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Whole-file hash mismatch: expected {expectedHash}, got {actual}.");
    }

    public Task FinalizeAsync(Guid transferId, CancellationToken ct)
    {
        var partial = PartialPath(transferId);
        var final = FinalPath(transferId);
        if (File.Exists(final)) File.Delete(final);
        File.Move(partial, final);
        _transfers.TryRemove(transferId, out _);
        return Task.CompletedTask;
    }

    public Task AbortAsync(Guid transferId, CancellationToken ct)
    {
        var partial = PartialPath(transferId);
        if (File.Exists(partial)) File.Delete(partial);
        _transfers.TryRemove(transferId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsPartialIntactAsync(Guid transferId, string filename, long expectedSizeBytes, TransferContext? context, CancellationToken ct)
    {
        TransferPathValidator.ThrowIfInvalid(filename, context);

        var relativePath = context?.RelativePath ?? "";
        var partialFile = new FileInfo(TransferPathValidator.ResolveFile(_rootPath, relativePath, filename + ".partial"));
        if (!partialFile.Exists) return Task.FromResult(false);
        if (partialFile.Length != expectedSizeBytes)
        {
            partialFile.Delete();
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    private string PartialPath(Guid transferId)
    {
        var (relativePath, filename) = _transfers[transferId];
        return TransferPathValidator.ResolveFile(_rootPath, relativePath, filename + ".partial");
    }

    private string FinalPath(Guid transferId)
    {
        var (relativePath, filename) = _transfers[transferId];
        return TransferPathValidator.ResolveFile(_rootPath, relativePath, filename);
    }
}
