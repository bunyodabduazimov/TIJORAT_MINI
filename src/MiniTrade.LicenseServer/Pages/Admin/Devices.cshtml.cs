using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class DevicesModel(LicenseDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public List<Device> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var q = db.Devices.Include(d => d.Activations).ThenInclude(a => a.License).ThenInclude(l => l!.Customer).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim().ToLower();
            q = q.Where(d => d.MachineName.ToLower().Contains(s) || d.InstallationId.Contains(s) || (d.LastIp != null && d.LastIp.Contains(s)));
        }
        Items = await q.OrderByDescending(d => d.LastSeenAt).Take(500).ToListAsync();
    }
}
