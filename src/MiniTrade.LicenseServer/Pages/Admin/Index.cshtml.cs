using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;
using MiniTrade.Licensing;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class IndexModel(LicenseDbContext db) : PageModel
{
    public int Customers, ActiveLicenses, ActiveTrials, ExpiringSoon, Blocked, Devices, ActiveActivations, OnlineToday;
    public List<License> Expiring { get; private set; } = new();
    public List<LicenseEvent> Events { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var now = DateTime.UtcNow;
        var soon = now.AddDays(30);
        Customers = await db.Customers.CountAsync();
        ActiveLicenses = await db.Licenses.CountAsync(l => l.Status == LicenseStatus.Active && (l.ExpiresAt == null || l.ExpiresAt > now));
        ActiveTrials = await db.Licenses.CountAsync(l => l.Type == LicenseType.Trial && l.Status == LicenseStatus.Active && l.ExpiresAt > now);
        Blocked = await db.Licenses.CountAsync(l => l.Status == LicenseStatus.Blocked);
        Devices = await db.Devices.CountAsync();
        ActiveActivations = await db.Activations.CountAsync(a => a.IsActive);
        OnlineToday = await db.Devices.CountAsync(d => d.LastSeenAt > now.AddDays(-1));
        Expiring = await db.Licenses.Include(l => l.Customer)
            .Where(l => l.Status == LicenseStatus.Active && l.ExpiresAt != null && l.ExpiresAt > now && l.ExpiresAt <= soon)
            .OrderBy(l => l.ExpiresAt).Take(10).ToListAsync();
        ExpiringSoon = await db.Licenses.CountAsync(l => l.Status == LicenseStatus.Active && l.ExpiresAt != null && l.ExpiresAt > now && l.ExpiresAt <= soon);
        Events = await db.Events.OrderByDescending(e => e.Id).Take(10).ToListAsync();
    }
}
