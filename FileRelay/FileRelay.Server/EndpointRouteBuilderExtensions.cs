using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileRelay.Server;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapChunkedTransfer(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ChunkedTransferOptions>();
        var base_ = options.BasePath.TrimEnd('/');

        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FileRelay.Server");

        if (options.RequireHttps && string.IsNullOrEmpty(options.ApiKey))
            throw new InvalidOperationException(
                "ChunkedTransferOptions: ApiKey must be set when RequireHttps is true. " +
                "An HTTPS-only endpoint with no API key allows any client to upload files.");

        if (!options.RequireHttps && string.IsNullOrEmpty(options.ApiKey))
            logger.LogWarning("FileRelay: RequireHttps and ApiKey are both disabled. " +
                "The transfer endpoint is completely open — any client can upload files.");

        if (!options.RequireHttps && !string.IsNullOrEmpty(options.ApiKey))
            logger.LogWarning("FileRelay: RequireHttps is disabled and the API key will be transmitted in cleartext. " +
                "This is only safe if this server is behind a reverse proxy that terminates TLS.");

        var group = app.MapGroup(base_);

        if (options.RequireHttps)
        {
            group.AddEndpointFilter(async (ctx, next) =>
            {
                if (!ctx.HttpContext.Request.IsHttps)
                    return Results.Problem(
                        statusCode: 421,
                        title: "HTTPS Required",
                        detail: "This endpoint does not accept plaintext HTTP connections.");
                return await next(ctx);
            });
        }

        if (options.ApiKey is { Length: > 0 } expectedKey)
        {
            group.AddEndpointFilter(async (ctx, next) =>
            {
                var auth = ctx.HttpContext.Request.Headers.Authorization.ToString();
                if (auth != $"Bearer {expectedKey}")
                    return Results.Unauthorized();
                return await next(ctx);
            });
        }

        group.MapGet("/ping", (ChunkedTransferOptions opts) =>
            Results.Ok(new FileRelay.Core.Models.ServerInfoResponse
            {
                BuildTime       = opts.ServerBuildTime,
                AssemblyVersion = typeof(TransferService).Assembly.GetName().Version?.ToString()
            }));

        group.MapPost("/negotiate", async (TransferService svc, FileRelay.Core.Models.TransferNegotiateRequest req, CancellationToken ct) =>
        {
            var result = await svc.NegotiateAsync(req, ct);
            return Results.Ok(result);
        });

        group.MapPost("/{transferId:guid}/chunk/{chunkIndex:int}", async (
            HttpContext context,
            TransferService svc,
            Guid transferId,
            int chunkIndex,
            CancellationToken ct) =>
        {
            var result = await svc.UploadChunkAsync(context, transferId, chunkIndex, ct);
            return result.StatusCode switch
            {
                200 => Results.Ok(new { result.IsComplete }),
                409 => Results.Conflict(new { result.Error }),
                404 => Results.NotFound(new { result.Error }),
                _ => Results.BadRequest(new { result.Error })
            };
        });

        group.MapGet("/{transferId:guid}/status", async (TransferService svc, Guid transferId, CancellationToken ct) =>
        {
            try
            {
                var status = await svc.GetStatusAsync(transferId, ct);
                return Results.Ok(status);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        return app;
    }
}
