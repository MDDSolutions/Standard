using Microsoft.Extensions.DependencyInjection;

namespace FileRelay.Server;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileRelay(this IServiceCollection services, Action<FileRelayOptions> configure)
    {
        var options = new FileRelayOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton<TransferService>();
        services.AddHostedService<TransferReconciliationService>();
        return services;
    }
}
