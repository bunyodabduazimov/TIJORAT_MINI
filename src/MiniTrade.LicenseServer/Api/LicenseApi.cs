using MiniTrade.LicenseServer.Services;
using MiniTrade.Licensing;

namespace MiniTrade.LicenseServer.Api;

/// <summary>Публичный HTTPS API лицензирования для настольного клиента.</summary>
public static class LicenseApi
{
    public const string RateLimitPolicy = "license-api";

    public static void MapLicenseApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/license").RequireRateLimiting(RateLimitPolicy).DisableAntiforgery();

        api.MapPost("/register", async (RegisterRequest req, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
            ToResult(await svc.RegisterTrialAsync(req, Ip(ctx), ct)));

        api.MapPost("/activate", async (ActivateRequest req, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
            ToResult(await svc.ActivateAsync(req, Ip(ctx), ct)));

        api.MapPost("/validate", async (ValidateRequest req, LicenseService svc, HttpContext ctx, CancellationToken ct) =>
            ToResult(await svc.ValidateAsync(req, Ip(ctx), ct)));

        app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
    }

    private static IResult ToResult(LicenseResponse r) => r.Success
        ? Results.Ok(r)
        : r.ErrorCode switch
        {
            LicenseErrorCodes.InvalidRequest => Results.BadRequest(r),
            LicenseErrorCodes.KeyNotFound => Results.NotFound(r),
            _ => Results.Json(r, statusCode: StatusCodes.Status403Forbidden)
        };

    private static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();
}
