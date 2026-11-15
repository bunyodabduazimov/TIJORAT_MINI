using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class CustomerModel(LicenseDbContext db) : PageModel
{
    public Customer Customer { get; private set; } = null!;
    [BindProperty] public CustomerInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var c = await db.Customers.Include(x => x.Licenses).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        Customer = c;
        Input = new CustomerInput { FullName = c.FullName, Company = c.Company, Phone = c.Phone, Country = c.Country, Email = c.Email, Notes = c.Notes };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var c = await db.Customers.FindAsync(id);
        if (c is null) return NotFound();
        if (string.IsNullOrWhiteSpace(Input.FullName))
        {
            TempData["Error"] = "Укажите ФИО клиента.";
            return RedirectToPage(new { id });
        }
        c.FullName = Input.FullName.Trim();
        c.Company = Input.Company?.Trim() ?? "";
        c.Phone = Input.Phone?.Trim() ?? "";
        c.Country = Input.NormalizedCountry;
        c.Email = Input.Email?.Trim();
        c.Notes = Input.Notes;
        await db.SaveChangesAsync();
        TempData["Message"] = "Изменения сохранены.";
        return RedirectToPage(new { id });
    }
}
