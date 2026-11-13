using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Api;
using MiniTrade.LicenseServer.Data;
using MiniTrade.LicenseServer.Services;

// Служебные команды
if (args.Length >= 2 && args[0] == "--hash-password")
{
    Console.WriteLine(AdminAuth.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ===== База лицензий: MySQL (по умолчанию) или SQLite для разработки =====
var provider = cfg["Database:Provider"] ?? "MySQL";
var connection = cfg.GetConnectionString("LicenseDb") ?? throw new InvalidOperationException("Не задана строка подключения LicenseDb");
builder.Services.AddDbContext<LicenseDbContext>(o =>
{
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) o.UseSqlite(connection);
    else o.UseMySQL(connection);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<AdminAuth>();
builder.Services.AddScoped<LicenseService>();

// ===== Защита API от перебора ключей =====
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(LicenseApi.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("Licensing:RateLimitPerMinute", 30), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("admin-login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
});

// ===== Панель администратора =====
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/admin/login";
        o.LogoutPath = "/admin/logout";
        o.Cookie.Name = "mt_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin");
    o.Conventions.AllowAnonymousToPage("/Admin/Login");
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

if (args.Contains("--export-public-key"))
{
    Console.WriteLine(app.Services.GetRequiredService<SigningKeyProvider>().PublicKeyPem);
    return;
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection).DataSource;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
    db.Database.EnsureCreated();
    _ = app.Services.GetRequiredService<SigningKeyProvider>(); // генерирует ключ при первом запуске
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/admin/error");
    app.UseHsts();
}
if (cfg.GetValue("Licensing:RequireHttps", true))
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/admin"));
app.MapLicenseApi();
app.MapRazorPages();

app.Run();

public partial class Program;
