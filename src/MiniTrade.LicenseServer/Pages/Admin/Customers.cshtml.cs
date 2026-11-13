using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class CustomerInput
{
    public string FullName { get; set; } = "";
    public string? Company { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
}

public class CustomersModel(LicenseDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty] public CustomerInput Input { get; set; } = new();
    public List<Customer> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var q = db.Customers.Include(c => c.Licenses).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim().ToLower();
            q = q.Where(c => c.FullName.ToLower().Contains(s) || c.Company.ToLower().Contains(s) || c.Phone.Contains(s));
        }
        Items = await q.OrderByDescending(c => c.Id).Take(300).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.FullName))
        {
            TempData["Error"] = "Укажите ФИО клиента.";
            return RedirectToPage();
        }
        var c = new Customer
        {
            FullName = Input.FullName.Trim(), Company = Input.Company?.Trim() ?? "", Phone = Input.Phone?.Trim() ?? "", Email = Input.Email?.Trim()
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        TempData["Message"] = "Клиент добавлен.";
        return Redirect($"/admin/customers/{c.Id}");
    }
}
