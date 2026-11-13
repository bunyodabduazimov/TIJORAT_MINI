using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;
using MiniTrade.LicenseServer.Services;
using MiniTrade.Licensing;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class LicenseInput
{
    public int? CustomerId { get; set; }
    public LicenseType Type { get; set; } = LicenseType.Annual;
    public int MaxDevices { get; set; } = LicenseDefaults.DefaultMaxDevices;
    public string? Notes { get; set; }
}

public class LicensesModel(LicenseDbContext db, LicenseService licenses) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
    [BindProperty] public LicenseInput Input { get; set; } = new();
    public List<License> Items { get; private set; } = new();
    public List<Customer> Customers { get; private set; } = new();

    public async Task OnGetAsync(int? customerId)
    {
        if (customerId is not null) Input.CustomerId = customerId;
        var now = DateTime.UtcNow;
        var q = db.Licenses.Include(l => l.Customer).Include(l => l.Activations).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim().ToLower();
            q = q.Where(l => l.Key.ToLower().Contains(s) || l.Customer!.FullName.ToLower().Contains(s) || l.Customer.Company.ToLower().Contains(s));
        }
        q = Filter switch
        {
            "active" => q.Where(l => l.Status == LicenseStatus.Active && (l.ExpiresAt == null || l.ExpiresAt > now)),
            "trial" => q.Where(l => l.Type == LicenseType.Trial),
            "expiring" => q.Where(l => l.Status == LicenseStatus.Active && l.ExpiresAt > now && l.ExpiresAt <= now.AddDays(30)),
            "expired" => q.Where(l => l.ExpiresAt != null && l.ExpiresAt <= now),
            "blocked" => q.Where(l => l.Status == LicenseStatus.Blocked),
            _ => q
        };
        Items = await q.OrderByDescending(l => l.Id).Take(500).ToListAsync();
        Customers = await db.Customers.OrderBy(c => c.FullName).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.CustomerId is null || await db.Customers.FindAsync(Input.CustomerId) is null)
        {
            TempData["Error"] = "Выберите клиента.";
            return RedirectToPage();
        }
        var now = DateTime.UtcNow;
        var license = new License
        {
            Key = await licenses.NewUniqueKeyAsync(HttpContext.RequestAborted),
            CustomerId = Input.CustomerId.Value,
            Type = Input.Type,
            MaxDevices = Math.Clamp(Input.MaxDevices, 1, 100),
            Notes = Input.Notes,
            ExpiresAt = Input.Type switch
            {
                LicenseType.Trial => now.AddDays(LicenseDefaults.TrialDays),
                LicenseType.Annual => now.AddDays(LicenseDefaults.AnnualDays),
                _ => null
            }
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        await licenses.AdminLogAsync("created", license.Id, null, $"{LicenseDefaults.TypeName(license.Type)}, устройств: {license.MaxDevices}");
        await db.SaveChangesAsync();
        TempData["Message"] = $"Создан ключ {license.Key}";
        return Redirect($"/admin/licenses/{license.Id}");
    }
}
