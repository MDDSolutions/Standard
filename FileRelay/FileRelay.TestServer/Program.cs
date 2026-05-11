using System.Reflection;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using FileRelay.Server;
using FileRelay.Storage.Sqlite;
using FileRelay.Storage.SqlServer;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection("FileRelay");

var httpPort  = cfg.GetValue<int>("HttpPort",0);
var httpsPort = cfg.GetValue<int>("HttpsPort");

if (httpPort <= 0 && httpsPort <= 0)
    throw new InvalidOperationException(
        "FileRelay config: at least one of HttpPort or HttpsPort must be a valid port number.");

var chunkSizeMB = cfg.GetValue<int>("ChunkSizeMB", 50);

builder.WebHost.ConfigureKestrel(options =>
{
    if (httpPort  > 0) options.ListenAnyIP(httpPort);
    if (httpsPort > 0) options.ListenAnyIP(httpsPort, o => o.UseHttps());
    FileRelayOptions.ConfigureKestrelLimits(options, chunkSizeMB);
});

builder.Services.AddSingleton<FaultInjector>();

builder.Services.AddFileRelay(options =>
{
    options.BasePath          = "/transfer";
    options.ChunkSizeMB       = chunkSizeMB;
    options.RequireHttps      = cfg.GetValue<bool>("RequireHttps", true);
    options.AllowHttpChunks   = cfg.GetValue<bool>("AllowHttpChunks", false);
    options.HttpPort          = httpPort;
    options.ServerReceiveMBps = cfg.GetValue<double>("ServerReceiveMBps", 0);
    options.ServerBuildTime   = MDDFoundation.Foundation.BuildTime(Assembly.GetExecutingAssembly());

    var userConfigs = cfg.GetSection("Users").Get<UserConfig[]>() ?? [];
    if (userConfigs.Length == 0)
        throw new InvalidOperationException("FileRelay config: Users must contain at least one entry.");

    options.Users = userConfigs.Select(u => new AppUser
    {
        AppId   = u.AppId,
        SeedKey = u.SeedKey,
        Targets = u.Targets.Select(p => (ITransferTarget)new LocalDirectoryTarget(ResolvePath(p))).ToArray()
    }).ToArray();

    options.OnComplete = new ConsoleCompleteHandler();

    //var dbPath = Path.Combine(AppContext.BaseDirectory, "transfers.db");
    //options.StateStore = new SqliteTransferStateStore(dbPath);
    //options.KeyStore   = new SqliteKeyStore(dbPath);

    var dbConnStr = "Server=MDD-SQL2022;Database=DBA;Trusted_Connection=True;Encrypt=False;";
    options.StateStore = new SqlServerTransferStateStore(dbConnStr);
    options.KeyStore = new SqlServerKeyStore(dbConnStr);
});

var app = builder.Build();

// Fault injection — intercepts chunk uploads before auth and returns 400 to trigger client retry.
// Arm via:  POST /test/inject-fault  { "chunkIndex": N }
var faultInjector = app.Services.GetRequiredService<FaultInjector>();
var faultLogger   = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FaultInjector");

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Method == "POST")
    {
        // Match .../{guid}/chunk/{int} from the end — works with any base path depth.
        var segs = (ctx.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 3 &&
            segs[^2] == "chunk" &&
            Guid.TryParse(segs[^3], out var tid) &&
            int.TryParse(segs[^1], out var cidx) &&
            faultInjector.TryConsume(tid, cidx))
        {
            faultLogger.LogWarning("[FaultInjector] Flipping bit 0 of chunk body — transfer {TransferId} chunk {ChunkIndex} will fail hash validation", tid, cidx);
            ctx.Request.Body = new CorruptingStream(ctx.Request.Body);
        }
    }
    await next(ctx);
});

app.MapPost("/test/inject-fault", (InjectFaultRequest req, FaultInjector faults) =>
{
    faults.Enqueue(req.ChunkIndex, req.TransferId);
    var target = req.TransferId.HasValue ? $"transfer {req.TransferId}" : "any transfer";
    return Results.Ok(new { Message = $"Fault queued: chunk {req.ChunkIndex} on {target}" });
});

app.MapFileRelay();
app.Run();

static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

class UserConfig
{
    public string   AppId   { get; set; } = "";
    public string   SeedKey { get; set; } = "";
    public string[] Targets { get; set; } = [];
}

record InjectFaultRequest(int ChunkIndex, Guid? TransferId = null);

class ConsoleCompleteHandler : ITransferCompleteHandler
{
    public Task<bool> OnValidateAsync(CompletedTransfer transfer, CancellationToken ct)
        => Task.FromResult(true);

    public Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct)
    {
        Console.WriteLine($"[Complete] [{transfer.AppId}] {transfer.Filename}  {transfer.FileSizeBytes:N0} bytes  id={transfer.TransferId}");
        return Task.CompletedTask;
    }
}
