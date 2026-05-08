using System.Reflection;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using FileRelay.Server;
using FileRelay.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(61488);                                      // HTTP — accepts from any interface
    options.ListenAnyIP(61489, o => o.UseHttps());                  // HTTPS — accepts from any interface
});
builder.Services.AddChunkedTransfer(options =>
{
    options.BasePath = "/transfer";
    options.ChunkSizeMB = 50;
    options.RequireHttps = false; // local test server only — never disable in production
    options.ApiKey = "test-key-abc123";
    options.Targets = [new LocalDirectoryTarget(Path.Combine(AppContext.BaseDirectory, "received"))];
    options.OnComplete = new ConsoleCompleteHandler();
    options.StateStore = new SqliteTransferStateStore("transfers.db");
    options.ServerReceiveMBps = 0; // throttle server receive to 100 MB/s to better simulate real-world conditions and test client-side throttling; set to 0 to disable
    options.ServerBuildTime = MDDFoundation.Foundation.BuildTime(Assembly.GetExecutingAssembly());
});

var app = builder.Build();
app.MapChunkedTransfer();
app.Run();

class ConsoleCompleteHandler : ITransferCompleteHandler
{
    public Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct)
    {
        Console.WriteLine($"[Complete] {transfer.Filename}  {transfer.FileSizeBytes:N0} bytes  id={transfer.TransferId}");
        return Task.CompletedTask;
    }
}
