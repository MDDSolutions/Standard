using System.Collections.Concurrent;
using FileRelay.Core;
using FileRelay.Core.Interfaces;

namespace FileRelay.Server;

public class LocalDirectoryTarget : ITransferTarget
{
    private readonly string _rootPath;
    private readonly ConcurrentDictionary<Guid, (string RelativePath, string Filename)> _transfers = new();

    public LocalDirectoryTarget(string rootPath)
    {
        _rootPath = rootPath;
        Directory.CreateDirectory(rootPath);
    }

    public Task InitializeAsync(Guid transferId, string filename, long fileSizeBytes, TransferContext? context, CancellationToken ct)
    {
        var relativePath = context?.RelativePath ?? "";
        _transfers[transferId] = (relativePath, filename);

        var dir = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(dir);

        var partialPath = PartialPath(transferId);
        using var fs = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.SetLength(fileSizeBytes);

        return Task.CompletedTask;
    }

    public Task<Stream> OpenChunkWriterAsync(Guid transferId, int chunkIndex, long offset, CancellationToken ct)
    {
        var fs = new FileStream(PartialPath(transferId), FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(fs);
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

    private string PartialPath(Guid transferId)
    {
        var (relativePath, filename) = _transfers[transferId];
        return Path.Combine(_rootPath, relativePath, filename + ".partial");
    }

    private string FinalPath(Guid transferId)
    {
        var (relativePath, filename) = _transfers[transferId];
        return Path.Combine(_rootPath, relativePath, filename);
    }
}
