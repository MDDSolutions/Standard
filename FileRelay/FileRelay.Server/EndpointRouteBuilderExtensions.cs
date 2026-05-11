using FileRelay.Core;
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
                {
                    // Chunk endpoints authenticate via per-chunk HMAC — API key never on the wire.
                    // When AllowHttpChunks is enabled, HTTP is safe for the data path.
                    if (options.AllowHttpChunks)
                    {
                        var rv = ctx.HttpContext.Request.RouteValues;
                        if (rv.ContainsKey("transferId") && rv.ContainsKey("chunkIndex"))
                            return await next(ctx);
                    }
                    return Results.Problem(
                        statusCode: 421,
                        title: "HTTPS Required",
                        detail: "This endpoint does not accept plaintext HTTP connections.");
                }
                return await next(ctx);
            });
        }

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var appId = ctx.HttpContext.Request.Headers["X-App-Id"].ToString();
            var user  = options.Users.FirstOrDefault(u => u.AppId == appId);
            if (user == null) return Results.Unauthorized();

            // Chunk endpoints authenticate via per-chunk HMAC token — the API key is never
            // sent on these requests, which makes HTTP data path safe to enable as long as the data itself is not sensitive.
            var routeValues = ctx.HttpContext.Request.RouteValues;
            if (routeValues.TryGetValue("transferId", out var tidVal) &&
                routeValues.TryGetValue("chunkIndex",  out var cidxVal) &&
                Guid.TryParse(tidVal?.ToString(),  out var transferId) &&
                int.TryParse(cidxVal?.ToString(),  out var chunkIndex))
            {
                var rawToken = ctx.HttpContext.Request.Headers["X-Chunk-Token"].ToString();
                byte[]? tokenBytes = null;
                try { tokenBytes = Convert.FromBase64String(rawToken); } catch { }
                if (tokenBytes == null) return Results.Unauthorized();

                var runIndexStr = ctx.HttpContext.Request.Headers["X-Run-Index"].ToString();
                var runIndex = int.TryParse(runIndexStr, out var ri) && ri >= 1 ? ri : 1;

                var chunkMacKeys = Array.Empty<string>();
                if (options.KeyStore != null)
                {
                    var chunkAuth = await options.KeyStore.AuthenticateChunkAsync(appId,
                        k => ChunkToken.Validate(tokenBytes, k, appId, transferId, chunkIndex, runIndex),
                        options.KeyGracePeriod);
                    if (chunkAuth == null) return Results.Unauthorized();
                    chunkMacKeys = chunkAuth.Value.MacKeys;
                }
                else if (user.SeedKey.Length > 0)
                {
                    if (!ChunkToken.Validate(tokenBytes, user.SeedKey, appId, transferId, chunkIndex, runIndex))
                        return Results.Unauthorized();
                    chunkMacKeys = [user.SeedKey];
                }
                // else: keyless user — X-App-Id alone is sufficient
                ctx.HttpContext.Items["AppUser"]      = user;
                ctx.HttpContext.Items["RunIndex"]     = runIndex;
                ctx.HttpContext.Items["ChunkMacKeys"] = chunkMacKeys;
                return await next(ctx);
            }

            // All other endpoints: Bearer authentication.
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
            if (!TransferPathValidator.TryValidate(req.Filename, req.Context, out var validationError))
                return Results.BadRequest(new { Error = validationError });

            var user    = (AppUser)context.Items["AppUser"]!;
            req.AppId   = user.AppId;
            var targets = user.Targets;
            TransferNegotiateResponse result;
            try
            {
                result = await svc.NegotiateAsync(req, targets, ct);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
            catch (OverflowException ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
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
                422 => Results.UnprocessableEntity(new { result.Error }),
                500 => Results.Problem(statusCode: 500, detail: result.Error),
                _   => Results.BadRequest(new { result.Error })
            };
        });

        group.MapGet("/{transferId:guid}/status", async (HttpContext context, TransferService svc, Guid transferId, CancellationToken ct) =>
        {
            try
            {
                var user = (AppUser)context.Items["AppUser"]!;
                var status = await svc.GetStatusAsync(transferId, user.AppId, ct);
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
