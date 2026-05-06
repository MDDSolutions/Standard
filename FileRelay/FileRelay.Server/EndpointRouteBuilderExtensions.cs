using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FileRelay.Server;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapChunkedTransfer(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ChunkedTransferOptions>();
        var base_ = options.BasePath.TrimEnd('/');

        app.MapPost($"{base_}/negotiate", async (TransferService svc, FileRelay.Core.Models.TransferNegotiateRequest req, CancellationToken ct) =>
        {
            var result = await svc.NegotiateAsync(req, ct);
            return Results.Ok(result);
        });

        app.MapPost($"{base_}/{{transferId:guid}}/chunk/{{chunkIndex:int}}", async (
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

        app.MapGet($"{base_}/{{transferId:guid}}/status", async (TransferService svc, Guid transferId, CancellationToken ct) =>
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
