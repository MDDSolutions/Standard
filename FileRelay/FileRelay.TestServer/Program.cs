using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using FileRelay.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddChunkedTransfer(options =>
{
    options.BasePath = "/transfer";
    options.ChunkSizeMB = 1;
    options.Targets = [new LocalDirectoryTarget(Path.Combine(AppContext.BaseDirectory, "received"))];
    options.OnComplete = new ConsoleCompleteHandler();
    options.SimulatedWanDelayPerBufferMs = 10; // ~8 MB/s; set to 0 to disable
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
