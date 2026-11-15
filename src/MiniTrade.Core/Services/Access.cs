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

    /// <summary>Максимальная скидка текущего пользователя, % (null — без ограничения).</summary>
    public static decimal? MaxDiscountPercent => _user?.MaxDiscountPercent;

    /// <summary>
    /// Списки для выбора: оставляет только доступные пользователю склады/кассы
    /// и ставит его склад/кассу по умолчанию первыми (экраны берут первый элемент).
    /// </summary>
    public static List<T> Scope<T>(List<T> items) where T : Entity
    {
        if (_user is not { } u) return items;
        HashSet<int>? allowed;
        int? preferred;
        if (typeof(T) == typeof(Warehouse)) { allowed = User.ParseIds(u.AllowedWarehouseIds); preferred = u.DefaultWarehouseId; }
        else if (typeof(T) == typeof(CashRegister)) { allowed = User.ParseIds(u.AllowedCashRegisterIds); preferred = u.DefaultCashRegisterId; }
        else return items;
        var result = allowed is null ? items : items.Where(i => allowed.Contains(i.Id)).ToList();
        if (preferred is { } p && result.FindIndex(i => i.Id == p) is var idx and > 0)
        {
            var item = result[idx];
            result.RemoveAt(idx);
            result.Insert(0, item);
        }
        return result;
    }

    /// <summary>Проверка при проведении: склад/касса должны быть доступны пользователю.</summary>
    public static void DemandWarehouse(int warehouseId)
    {
        if (_user is { } u && User.ParseIds(u.AllowedWarehouseIds) is { } set && !set.Contains(warehouseId))
            throw new BusinessException("Нет доступа к выбранному складу. Обратитесь к администратору.");
    }

    public static void DemandCashRegister(int cashRegisterId)
    {
        if (_user is { } u && User.ParseIds(u.AllowedCashRegisterIds) is { } set && !set.Contains(cashRegisterId))
            throw new BusinessException("Нет доступа к выбранной кассе. Обратитесь к администратору.");
    }
}
