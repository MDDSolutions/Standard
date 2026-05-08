using System.Reflection;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using FileRelay.Server;
using FileRelay.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection("FileRelay");

var httpPort  = cfg.GetValue<int>("HttpPort");
var httpsPort = cfg.GetValue<int>("HttpsPort");

if (httpPort <= 0 && httpsPort <= 0)
    throw new InvalidOperationException(
        "FileRelay config: at least one of HttpPort or HttpsPort must be a valid port number.");

var chunkSizeMB = cfg.GetValue<int>("ChunkSizeMB", 50);

builder.WebHost.ConfigureKestrel(options =>
{
    if (httpPort  > 0) options.ListenAnyIP(httpPort);
    if (httpsPort > 0) options.ListenAnyIP(httpsPort, o => o.UseHttps());
    ChunkedTransferOptions.ConfigureKestrelLimits(options, chunkSizeMB);
});

builder.Services.AddChunkedTransfer(options =>
{
    options.BasePath          = "/transfer";
    options.ChunkSizeMB       = chunkSizeMB;
    options.RequireHttps      = cfg.GetValue<bool>("RequireHttps", true);
    options.ApiKey            = cfg.GetValue<string>("ApiKey");
    options.ServerReceiveMBps = cfg.GetValue<double>("ServerReceiveMBps", 0);
    options.ServerBuildTime   = MDDFoundation.Foundation.BuildTime(Assembly.GetExecutingAssembly());

    var targetPaths = cfg.GetSection("Targets").Get<string[]>() ?? [];
    if (targetPaths.Length == 0)
        throw new InvalidOperationException("FileRelay config: Targets must contain at least one path.");

    options.Targets   = targetPaths.Select(p => (ITransferTarget)new LocalDirectoryTarget(ResolvePath(p))).ToArray();
    options.OnComplete = new ConsoleCompleteHandler();
    options.StateStore = new SqliteTransferStateStore(Path.Combine(AppContext.BaseDirectory, "transfers.db"));
});

var app = builder.Build();
app.MapChunkedTransfer();
app.Run();

static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

class ConsoleCompleteHandler : ITransferCompleteHandler
{
    public Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct)
    {
        Console.WriteLine($"[Complete] {transfer.Filename}  {transfer.FileSizeBytes:N0} bytes  id={transfer.TransferId}");
        return Task.CompletedTask;
    }
}
