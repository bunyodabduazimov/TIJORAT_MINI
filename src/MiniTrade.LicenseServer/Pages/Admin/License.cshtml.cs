using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;
using MiniTrade.LicenseServer.Services;
using MiniTrade.Licensing;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class LicenseModel(LicenseDbContext db, LicenseService licenses) : PageModel
{
    public License License { get; private set; } = null!;
    public List<LicenseEvent> Events { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var l = await db.Licenses.Include(x => x.Customer).Include(x => x.Activations).ThenInclude(a => a.Device)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (l is null) return NotFound();
        License = l;
        Events = await db.Events.Where(e => e.LicenseId == id).OrderByDescending(e => e.Id).Take(100).ToListAsync();
        return Page();
    }

    public Task<IActionResult> OnPostExtendAsync(int id, int days) => Change(id, l =>
    {
        if (l.Type == LicenseType.Perpetual) return "Бессрочная лицензия не требует продления.";
        days = Math.Clamp(days, 1, 3650);
        var now = DateTime.UtcNow;
        var start = l.ExpiresAt is { } e && e > now ? e : now;
        l.ExpiresAt = start.AddDays(days);
        if (l.Type == LicenseType.Trial && days >= 365) l.Type = LicenseType.Annual;
        return null;
    }, "extended", l => $"До {Fmt.Date(l.ExpiresAt)}");

    public Task<IActionResult> OnPostUpdateAsync(int id, LicenseType type, DateTime? expires, int maxDevices) => Change(id, l =>
    {
        l.Type = type;
        l.MaxDevices = Math.Clamp(maxDevices, 1, 100);
        if (type == LicenseType.Perpetual) l.ExpiresAt = null;
        else if (expires is { } d) l.ExpiresAt = DateTime.SpecifyKind(d.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime();
        else if (l.ExpiresAt is null) return "Укажите дату окончания для срочной лицензии.";
        return null;
    }, "updated", l => $"{LicenseDefaults.TypeName(l.Type)}, до {Fmt.Date(l.ExpiresAt)}, устройств: {l.MaxDevices}");

    public Task<IActionResult> OnPostBlockAsync(int id, string? reason) => Change(id, l =>
    {
        l.Status = LicenseStatus.Blocked;
        l.BlockReason = string.IsNullOrWhiteSpace(reason) ? "Заблокирована администратором" : reason.Trim();
        return null;
    }, "blocked", l => l.BlockReason);

    public Task<IActionResult> OnPostUnblockAsync(int id) => Change(id, l =>
    {
        l.Status = LicenseStatus.Active;
        l.BlockReason = null;
        return null;
    }, "unblocked", _ => null);

    public async Task<IActionResult> OnPostDeactivateAsync(int id, int activationId)
    {
        var a = await db.Activations.Include(x => x.Device).FirstOrDefaultAsync(x => x.Id == activationId && x.LicenseId == id);
        if (a is null) return NotFound();
        a.IsActive = false;
        a.DeactivatedAt = DateTime.UtcNow;
        await licenses.AdminLogAsync("deactivated", id, a.DeviceId, a.Device?.MachineName);
        await db.SaveChangesAsync();
        TempData["Message"] = "Устройство отвязано от лицензии.";
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> Change(int id, Func<License, string?> action, string eventName, Func<License, string?> details)
    {
        var l = await db.Licenses.FindAsync(id);
        if (l is null) return NotFound();
        var error = action(l);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToPage(new { id });
        }
        await licenses.AdminLogAsync(eventName, id, null, details(l));
        await db.SaveChangesAsync();
        TempData["Message"] = "Изменения сохранены.";
        return RedirectToPage(new { id });
    }
}
