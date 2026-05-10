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

        if (options.Users.Count == 0)
            throw new InvalidOperationException(
                "ChunkedTransferOptions: at least one user must be configured. " +
                "Users define the target paths where uploaded files are written. " +
                "A user with no ApiKey is valid and makes that user's transfers unauthenticated.");

        var anyKeyless  = options.Users.Any(u => u.ApiKey.Length == 0);
        var anyKeyed    = options.Users.Any(u => u.ApiKey.Length > 0);

        if (options.RequireHttps && anyKeyless)
            throw new InvalidOperationException(
                "ChunkedTransferOptions: all users must have an ApiKey when RequireHttps is true. " +
                "A user with no ApiKey on an HTTPS-only endpoint allows any client to upload as that user.");

        if (!options.RequireHttps && anyKeyed)
            logger.LogWarning("FileRelay: RequireHttps is disabled and API keys will be transmitted in cleartext. " +
                "This is only safe if this server is behind a reverse proxy that terminates TLS.");

        if (!options.RequireHttps && !anyKeyed)
            logger.LogWarning("FileRelay: no users have an ApiKey configured. " +
                "Any client that sends a valid X-App-Id can upload files.");

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

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var appId = ctx.HttpContext.Request.Headers["X-App-Id"].ToString();
            var user  = options.Users.FirstOrDefault(u => u.AppId == appId);
            if (user == null)
                return Results.Unauthorized();

            if (user.ApiKey.Length > 0)
            {
                var auth = ctx.HttpContext.Request.Headers.Authorization.ToString();
                if (auth != $"Bearer {user.ApiKey}")
                    return Results.Unauthorized();
            }

            ctx.HttpContext.Items["AppUser"] = user;
            return await next(ctx);
        });

        group.MapGet("/ping", (ChunkedTransferOptions opts) =>
            Results.Ok(new FileRelay.Core.Models.ServerInfoResponse
            {
                BuildTime       = opts.ServerBuildTime,
                AssemblyVersion = typeof(TransferService).Assembly.GetName().Version?.ToString()
            }));

        group.MapPost("/negotiate", async (HttpContext context, TransferService svc, FileRelay.Core.Models.TransferNegotiateRequest req, CancellationToken ct) =>
        {
            var user = context.Items["AppUser"] as AppUser;
            req.AppId = user?.AppId ?? "";
            var targets = user?.Targets ?? Array.Empty<FileRelay.Core.Interfaces.ITransferTarget>();
            var result = await svc.NegotiateAsync(req, targets, ct);
            return Results.Ok(result);
        });

        group.MapPost("/{transferId:guid}/chunk/{chunkIndex:int}", async (
            HttpContext context,
            TransferService svc,
            Guid transferId,
            int chunkIndex,
            CancellationToken ct) =>
        {
            var user = context.Items["AppUser"] as AppUser;
            var result = await svc.UploadChunkAsync(context, transferId, chunkIndex, user?.AppId ?? "", ct);
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
