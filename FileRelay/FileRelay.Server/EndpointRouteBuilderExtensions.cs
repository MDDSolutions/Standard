using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileRelay.Server;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapFileRelay(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<FileRelayOptions>();
        var base_ = options.BasePath.TrimEnd('/');

        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FileRelay.Server");

        if (options.Users.Count == 0)
            throw new InvalidOperationException(
                "FileRelayOptions: at least one user must be configured. " +
                "Users define the target paths where uploaded files are written. " +
                "A user with no SeedKey is valid and makes that user's transfers unauthenticated.");

        var anyKeyless = options.Users.Any(u => u.SeedKey.Length == 0);
        var anyKeyed   = options.Users.Any(u => u.SeedKey.Length > 0);

        if (options.RequireHttps && anyKeyless)
            throw new InvalidOperationException(
                "FileRelayOptions: all users must have a SeedKey when RequireHttps is true. " +
                "A user with no SeedKey on an HTTPS-only endpoint allows any client to upload as that user.");

        if (!options.RequireHttps && anyKeyed)
            logger.LogWarning("FileRelay: RequireHttps is disabled and API keys will be transmitted in cleartext. " +
                "This is only safe if this server is behind a reverse proxy that terminates TLS.");

        if (!options.RequireHttps && !anyKeyed)
            logger.LogWarning("FileRelay: no users have a SeedKey configured. " +
                "Any client that sends a valid X-App-Id can upload files.");

        // Seed the key store on startup — inserts only if no entry exists yet for each AppId.
        if (options.KeyStore != null)
        {
            foreach (var user in options.Users)
                options.KeyStore.SeedAsync(user.AppId, user.SeedKey).GetAwaiter().GetResult();
        }

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
            if (user == null) return Results.Unauthorized();

            KeyAuthResult? keyResult = null;

            if (options.KeyStore != null)
            {
                var bearer = ctx.HttpContext.Request.Headers.Authorization.ToString();
                var key = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer["Bearer ".Length..] : "";
                keyResult = await options.KeyStore.AuthenticateAsync(appId, key, options.KeyGracePeriod);
                if (keyResult == null) return Results.Unauthorized();

                if (keyResult.Status != KeyStatus.Current)
                    ctx.HttpContext.Response.Headers["X-Key-Status"] = keyResult.Status switch
                    {
                        KeyStatus.PreviousGracePending => "previous-grace-pending",
                        KeyStatus.PreviousGraceActive  => "previous-grace-active",
                        _                              => "previous"
                    };
            }
            else if (user.SeedKey.Length > 0)
            {
                var auth = ctx.HttpContext.Request.Headers.Authorization.ToString();
                if (auth != $"Bearer {user.SeedKey}") return Results.Unauthorized();
            }

            ctx.HttpContext.Items["AppUser"]   = user;
            ctx.HttpContext.Items["KeyStatus"] = keyResult?.Status;
            return await next(ctx);
        });

        group.MapGet("/ping", (FileRelayOptions opts) =>
            Results.Ok(new ServerInfoResponse
            {
                BuildTime       = opts.ServerBuildTime,
                AssemblyVersion = typeof(TransferService).Assembly.GetName().Version?.ToString()
            }));

        group.MapPost("/negotiate", async (HttpContext context, TransferService svc, TransferNegotiateRequest req, CancellationToken ct) =>
        {
            var user    = (AppUser)context.Items["AppUser"]!;
            req.AppId   = user.AppId;
            var targets = user.Targets;
            var result  = await svc.NegotiateAsync(req, targets, ct);
            return Results.Ok(result);
        });

        group.MapPost("/{transferId:guid}/chunk/{chunkIndex:int}", async (
            HttpContext context,
            TransferService svc,
            Guid transferId,
            int chunkIndex,
            CancellationToken ct) =>
        {
            var user   = (AppUser)context.Items["AppUser"]!;
            var result = await svc.UploadChunkAsync(context, transferId, chunkIndex, user.AppId, ct);
            return result.StatusCode switch
            {
                200 => Results.Ok(new { result.IsComplete }),
                409 => Results.Conflict(new { result.Error }),
                404 => Results.NotFound(new { result.Error }),
                _   => Results.BadRequest(new { result.Error })
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

        group.MapPost("/rotate-key", async (HttpContext context, FileRelayOptions opts, RotateKeyRequest req, CancellationToken ct) =>
        {
            if (opts.KeyStore == null)
                return Results.Problem(statusCode: 501, title: "Key rotation not configured",
                    detail: "No key store is configured on this server.");

            var keyStatus = context.Items["KeyStatus"] as KeyStatus?;
            if (keyStatus == KeyStatus.PreviousGraceActive)
                return Results.Problem(statusCode: 403, title: "Rotation not permitted",
                    detail: "The new key has already been used. Use the current key to rotate.");

            if (await opts.KeyStore.HasActiveGracePeriodAsync(((AppUser)context.Items["AppUser"]!).AppId))
                return Results.Problem(statusCode: 403, title: "Rotation not permitted",
                    detail: "A grace period is currently active. Wait for it to expire before rotating again.");

            var user = (AppUser)context.Items["AppUser"]!;

            byte[] clientEntropy;
            try   { clientEntropy = Convert.FromBase64String(req.ClientEntropy ?? ""); }
            catch { clientEntropy = Array.Empty<byte>(); }

            var newKey = await opts.KeyStore.RotateAsync(user.AppId, clientEntropy);
            return Results.Ok(new RotateKeyResponse { NewKey = newKey });
        });

        return app;
    }
}
