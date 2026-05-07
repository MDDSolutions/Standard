using Microsoft.Extensions.DependencyInjection;

namespace FileRelay.Server;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChunkedTransfer(this IServiceCollection services, Action<ChunkedTransferOptions> configure)
    {
        var options = new ChunkedTransferOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton<TransferService>();
        services.AddHostedService<TransferReconciliationService>();
        return services;
    }
}
