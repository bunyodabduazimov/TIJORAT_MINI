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
    /// <summary>Admin — у пользователя роль-администратор (все права); иначе права задаёт <see cref="AccessRole"/>.</summary>
    public UserRole Role { get; set; } = UserRole.Cashier;
    /// <summary>Роль с набором разрешений.</summary>
    public int? AccessRoleId { get; set; }
    public AccessRole? AccessRole { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? Phone { get; set; }
    /// <summary>Комментарий администратора (смена, должность и т. п.).</summary>
    public string? Note { get; set; }
    /// <summary>Устарело: прежние индивидуальные права по разделам. Не используется.</summary>
    public Permission? Permissions { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Компьютер последнего входа.</summary>
    public string? LastLoginMachine { get; set; }
    public int LoginCount { get; set; }

    public bool IsAdmin => Role == UserRole.Admin || AccessRole?.IsAdmin == true;

    /// <summary>Разрешено ли действие (ключ из <see cref="Perm"/>).</summary>
    public bool Can(string permission) => IsAdmin || AccessRole?.Has(permission) == true;
}

/// <summary>Устаревшие права по разделам (флаги) — только для переноса старых настроек в роли.</summary>
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
    Production = 512,
    All = Dashboard | Finance | Sales | Purchases | Warehouse | Catalog | Reports | AuditLog | Production
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
    /// <summary>Страна (код ISO 3166-1, например TJ): задаёт валюту и телефонный код.</summary>
    public string? Country { get; set; } = "TJ";
    public string Currency { get; set; } = "TJS";
    /// <summary>Разрешить продажу в минус (по умолчанию запрещено).</summary>
    public bool AllowNegativeStock { get; set; }
    /// <summary>Префикс весового штрихкода (2 цифры). Штрихкод весов EAN-13: ПП ККККК ВВВВВ К — префикс, код товара, вес в граммах, контрольная цифра.</summary>
    public string? ScalePrefix { get; set; } = "20";
    /// <summary>Метод списания себестоимости (по умолчанию FIFO).</summary>
    public CostingMethod CostingMethod { get; set; } = CostingMethod.Fifo;
    /// <summary>Права роли «Менеджер» (null — по умолчанию).</summary>
    public Permission? ManagerPermissions { get; set; }
    /// <summary>Права роли «Кассир» (null — по умолчанию).</summary>
    public Permission? CashierPermissions { get; set; }
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
