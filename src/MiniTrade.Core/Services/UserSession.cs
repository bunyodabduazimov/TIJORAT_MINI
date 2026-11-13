using MiniTrade.Core.Domain;

namespace MiniTrade.Core.Services;

/// <summary>Текущий авторизованный пользователь приложения.</summary>
public class UserSession
{
    public User? CurrentUser { get; private set; }
    public int? UserId => CurrentUser?.Id;
    public bool IsAdmin => CurrentUser?.Role == UserRole.Admin;

    public void SignIn(User user)
    {
        CurrentUser = user;
        AuditContext.Set(user.Id, string.IsNullOrWhiteSpace(user.FullName) ? user.Login : user.FullName);
    }

    public void SignOut()
    {
        CurrentUser = null;
        AuditContext.Set(null, null);
    }
}

/// <summary>
/// Автор изменений для журнала действий. Контексты базы создаются фабрикой без доступа к DI,
/// поэтому текущий пользователь хранится здесь (в программе один вошедший пользователь на процесс).
/// </summary>
public static class AuditContext
{
    private static volatile Author? _author;

    public static int? UserId => _author?.Id;
    public static string UserName => _author?.Name ?? "Система";

    public static void Set(int? userId, string? name) =>
        _author = name is null ? null : new Author(userId, name);

    private sealed record Author(int? Id, string Name);
}
