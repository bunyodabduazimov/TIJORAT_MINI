using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MiniTrade.LicenseServer.Data;
using MiniTrade.LicenseServer.Services;
using MiniTrade.Licensing;

namespace MiniTrade.Tests;

public sealed class LicenseServerFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-license-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:LicenseDb", $"Data Source={Path.Combine(_dir, "license.db")}");
        builder.UseSetting("Licensing:PrivateKeyPath", Path.Combine(_dir, "keys", "signing.pem"));
        builder.UseSetting("Licensing:RequireHttps", "false");
        builder.UseSetting("Licensing:RateLimitPerMinute", "1000");
        builder.UseSetting("Admin:Username", "admin");
        builder.UseSetting("Admin:Password", "secret");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class LicenseServerTests : IClassFixture<LicenseServerFactory>
{
    private readonly LicenseServerFactory _factory;
    private readonly HttpClient _http;

    public LicenseServerTests(LicenseServerFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    private static DeviceInfo NewDevice(string? fingerprint = null) =>
        new(Guid.NewGuid().ToString(), fingerprint ?? Convert.ToHexString(Guid.NewGuid().ToByteArray()), "PC", "1.0.0");

    private LicensePayload Verify(LicenseResponse r)
    {
        Assert.True(r.Success, r.Message);
        var keys = _factory.Services.GetRequiredService<SigningKeyProvider>();
        using var pub = LicenseTokenCodec.LoadPublicKey(keys.PublicKeyPem);
        Assert.True(LicenseTokenCodec.TryVerify(r.Token, pub, out var payload));
        return payload!;
    }

    private async Task<LicenseResponse> Post(string path, object body)
    {
        var resp = await _http.PostAsJsonAsync($"/api/v1/license/{path}", body);
        return (await resp.Content.ReadFromJsonAsync<LicenseResponse>())!;
    }

    [Fact]
    public async Task Register_issues_14_day_trial_once_per_device()
    {
        var device = NewDevice();
        var payload = Verify(await Post("register", new RegisterRequest(device, "Иван", "ООО Тест", "+992900000000")));
        Assert.Equal(LicenseType.Trial, payload.Type);
        Assert.Equal(device.InstallationId, payload.InstallationId);
        Assert.InRange((payload.ExpiresAtUtc!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);

        // Переустановка на том же оборудовании продолжает тот же триал, а не начинает новый
        var reinstall = device with { InstallationId = Guid.NewGuid().ToString() };
        var again = Verify(await Post("register", new RegisterRequest(reinstall, "Иван", "", "+992900000000")));
        Assert.Equal(payload.LicenseKey, again.LicenseKey);
        Assert.Equal(payload.ExpiresAtUtc, again.ExpiresAtUtc);

        Verify(await Post("validate", new ValidateRequest(reinstall, again.LicenseKey)));
    }

    [Fact]
    public async Task Activation_respects_device_limit_and_blocking()
    {
        string key;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<LicenseService>();
            key = await svc.NewUniqueKeyAsync(default);
            db.Licenses.Add(new License { Key = key, Type = LicenseType.Perpetual, Customer = new Customer { FullName = "Клиент" } });
            await db.SaveChangesAsync();
        }

        var d1 = NewDevice();
        var p = Verify(await Post("activate", new ActivateRequest(d1, key.ToLower())));
        Assert.Equal(LicenseType.Perpetual, p.Type);
        Assert.Null(p.ExpiresAtUtc);
        Verify(await Post("activate", new ActivateRequest(d1, key))); // повторная активация того же устройства

        var second = await _http.PostAsJsonAsync("/api/v1/license/activate", new ActivateRequest(NewDevice(), key));
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(LicenseErrorCodes.DeviceLimit, (await second.Content.ReadFromJsonAsync<LicenseResponse>())!.ErrorCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Single(l => l.Key == key).Status = LicenseStatus.Blocked;
            await db.SaveChangesAsync();
        }
        var blocked = await Post("validate", new ValidateRequest(d1, key));
        Assert.False(blocked.Success);
        Assert.Equal(LicenseErrorCodes.Blocked, blocked.ErrorCode);
    }

    [Fact]
    public async Task Unknown_key_and_bad_requests_are_rejected()
    {
        var notFound = await _http.PostAsJsonAsync("/api/v1/license/activate", new ActivateRequest(NewDevice(), "MT-AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        var bad = await _http.PostAsJsonAsync("/api/v1/license/register", new RegisterRequest(NewDevice() with { InstallationId = "x" }, "a", "b", "c"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Admin_panel_requires_login()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/admin/licenses");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/admin/login", resp.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/login")).StatusCode);
    }
}
