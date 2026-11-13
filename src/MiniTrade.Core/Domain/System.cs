namespace MiniTrade.Core.Domain;

public enum UserRole
{
    Admin = 1,
    Manager = 2,
    Cashier = 3
}

public class User : Entity
{
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Cashier;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? Phone { get; set; }
    /// <summary>Комментарий администратора (смена, должность и т. п.).</summary>
    public string? Note { get; set; }
    /// <summary>Индивидуальные права; null — права роли по умолчанию. Администратору доступно всё.</summary>
    public Permission? Permissions { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Компьютер последнего входа.</summary>
    public string? LastLoginMachine { get; set; }
    public int LoginCount { get; set; }

    /// <summary>Действующие права пользователя.</summary>
    public Permission EffectivePermissions =>
        Role == UserRole.Admin ? Permission.All : Permissions ?? RolePermissions.Default(Role);

    public bool Can(Permission p) => (EffectivePermissions & p) == p;
}

/// <summary>Права доступа к разделам программы.</summary>
[Flags]
public enum Permission : long
{
    None = 0,
    Dashboard = 1,
    Finance = 2,
    Sales = 4,
    Purchases = 8,
    Warehouse = 16,
    Catalog = 32,
    Reports = 64,
    AuditLog = 256,
    All = Dashboard | Finance | Sales | Purchases | Warehouse | Catalog | Reports | AuditLog
}

/// <summary>Права ролей по умолчанию.</summary>
public static class RolePermissions
{
    public static Permission Default(UserRole role) => role switch
    {
        UserRole.Admin => Permission.All,
        UserRole.Manager => Permission.Dashboard | Permission.Finance | Permission.Sales | Permission.Purchases
                            | Permission.Warehouse | Permission.Catalog | Permission.Reports,
        _ => Permission.Dashboard | Permission.Sales
    };
}

public class CompanySettings : Entity
{
    public string Name { get; set; } = "Моя компания";
    /// <summary>ИНН.</summary>
    public string? TaxId { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>Текст внизу товарного чека (благодарность, условия возврата).</summary>
    public string? ReceiptFooter { get; set; }
    public string Currency { get; set; } = "TJS";
    /// <summary>Разрешить продажу в минус (по умолчанию запрещено).</summary>
    public bool AllowNegativeStock { get; set; }
    /// <summary>Метод списания себестоимости (по умолчанию FIFO).</summary>
    public CostingMethod CostingMethod { get; set; } = CostingMethod.Fifo;
}

/// <summary>Метод оценки себестоимости при списании товара.</summary>
public enum CostingMethod
{
    /// <summary>По партиям: первым списывается товар из самого раннего прихода.</summary>
    Fifo = 0,
    /// <summary>По средневзвешенной себестоимости остатка.</summary>
    Average = 1
}

/// <summary>Вид действия в журнале действий пользователей.</summary>
public enum AuditAction
{
    Create = 1,
    Update = 2,
    Delete = 3,
    Login = 4,
    Logout = 5,
    /// <summary>Мягкое удаление (запись ушла в архив).</summary>
    Archive = 6,
    /// <summary>Возврат записи из архива.</summary>
    Restore = 7
}

/// <summary>Запись журнала действий: кто, когда и что создал, изменил или удалил.</summary>
public class AuditLog : Entity
{
    public DateTime Date { get; set; } = DateTime.Now;
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>Имя компьютера (при общей базе MySQL видно, с какого устройства).</summary>
    public string? Machine { get; set; }
    public AuditAction Action { get; set; }
    /// <summary>Тип объекта (имя класса: Product, Sale …).</summary>
    public string EntityType { get; set; } = "";
    public int? EntityId { get; set; }
    /// <summary>Название или номер объекта на момент действия.</summary>
    public string? EntityName { get; set; }
    /// <summary>Подробности: изменённые поля «было → стало», сумма документа и т. п.</summary>
    public string? Details { get; set; }
}
