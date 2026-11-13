using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MiniTrade.LicenseServer.Services;

namespace MiniTrade.LicenseServer.Pages.Admin;

[EnableRateLimiting("admin-login")]
public class LoginModel(AdminAuth auth) : PageModel
{
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!auth.Validate(Username, Password))
        {
            Error = "Неверный логин или пароль.";
            return Page();
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, Username!), new Claim(ClaimTypes.Role, "admin")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin");
    }
}
