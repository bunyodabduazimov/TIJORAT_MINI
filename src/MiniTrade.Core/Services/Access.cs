using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;

namespace MiniTrade.Core.Services;

/// <summary>
/// Проверка прав текущего пользователя. Как и <see cref="AuditContext"/>, хранится статически:
/// в программе один вошедший пользователь на процесс. Без входа (служебные операции, тесты) разрешено всё.
/// </summary>
public static class Access
{
    private static volatile User? _user;

    internal static void Set(User? user) => _user = user;

    public static bool Can(string permission) => _user is not { } u || u.Can(permission);

    /// <summary>Бросает понятную ошибку, если действие запрещено ролью пользователя.</summary>
    public static void Demand(string permission)
    {
        if (!Can(permission))
            throw new BusinessException("Недостаточно прав: «{0}». Обратитесь к администратору.", PermissionCatalog.Describe(permission));
    }
}
