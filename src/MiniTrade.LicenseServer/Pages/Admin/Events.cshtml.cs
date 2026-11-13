using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MiniTrade.LicenseServer.Data;

namespace MiniTrade.LicenseServer.Pages.Admin;

public class EventsModel(LicenseDbContext db) : PageModel
{
    public List<LicenseEvent> Items { get; private set; } = new();

    public async Task OnGetAsync() => Items = await db.Events.OrderByDescending(e => e.Id).Take(500).ToListAsync();

    public static string ActionName(string action) => action switch
    {
        "trial_registered" => "Регистрация (пробный период)",
        "trial_resumed" => "Повторная установка (пробный)",
        "activated" => "Активация ключа",
        "activation_failed" => "Неудачная активация",
        "activation_denied" => "Отказ: лимит устройств",
        "created" => "Создан ключ",
        "extended" => "Продление",
        "updated" => "Изменение параметров",
        "blocked" => "Блокировка",
        "unblocked" => "Разблокировка",
        "deactivated" => "Отвязка устройства",
        _ => action
    };
}
