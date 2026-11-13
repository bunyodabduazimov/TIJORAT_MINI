using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Core.Data;

/// <summary>
/// Собирает журнал действий из изменений контекста: создание, изменение (поля «было → стало»),
/// удаление и архивирование справочников, документов, кассовых операций, пользователей и настроек.
/// Служебные регистры (остатки, движение товара, строки документов, нумерация) не пишутся —
/// они меняются вместе со своим документом, который уже попадает в журнал.
/// </summary>
internal static class AuditTracker
{
    private static readonly HashSet<Type> Tracked =
    [
        typeof(Category), typeof(Product), typeof(ProductPhoto), typeof(CounterpartyGroup), typeof(Counterparty),
        typeof(Warehouse), typeof(CashRegister), typeof(FinanceArticle),
        typeof(Sale), typeof(SaleReturn), typeof(Purchase), typeof(PurchaseReturn), typeof(StockTransfer), typeof(InventoryCount),
        typeof(CashTransaction), typeof(User), typeof(CompanySettings)
    ];

    private static readonly Type[] Documents = [typeof(Sale), typeof(SaleReturn), typeof(Purchase), typeof(PurchaseReturn), typeof(StockTransfer), typeof(InventoryCount)];

    /// <summary>Поля, которые меняются автоматически при проведении документов, — не шум в журнале.</summary>
    private static readonly HashSet<string> Ignored = ["Id", "Balance", "CostTotal", "ReturnedTotal", "UpdatedAt", "Currency", "CreatedAt", "Thumbnail",
        "LastLoginAt", "LastLoginMachine", "LoginCount"];

    private static readonly Dictionary<string, string> FieldNames = new()
    {
        ["Name"] = "Название", ["Sku"] = "Артикул", ["Barcode"] = "Штрихкод", ["Unit"] = "Ед. изм.",
        ["CategoryId"] = "Категория", ["PurchasePrice"] = "Закупочная цена", ["SalePrice"] = "Цена продажи",
        ["MinStock"] = "Мин. остаток", ["IsActive"] = "Активен", ["IsDeleted"] = "В архиве", ["Phone"] = "Телефон",
        ["Address"] = "Адрес", ["Note"] = "Примечание", ["Description"] = "Описание", ["GroupId"] = "Группа",
        ["Login"] = "Логин", ["FullName"] = "ФИО", ["Role"] = "Роль", ["Permissions"] = "Права",["PasswordHash"] = "Пароль",
        ["TaxId"] = "ИНН", ["Email"] = "Email", ["ReceiptFooter"] = "Текст чека", ["AllowNegativeStock"] = "Продажа в минус",
        ["Direction"] = "Направление", ["Amount"] = "Сумма", ["Comment"] = "Комментарий", ["Date"] = "Дата",
        ["Total"] = "Сумма", ["PaidAmount"] = "Оплачено", ["CustomerId"] = "Покупатель", ["SupplierId"] = "Поставщик",
        ["WarehouseId"] = "Склад", ["CashRegisterId"] = "Касса", ["Category"] = "Статья", ["CounterpartyId"] = "Контрагент",
        ["Image"] = "Картинка", ["Data"] = "Фото", ["Number"] = "Номер", ["DifferenceAmount"] = "Расхождение"
    };

    internal sealed record Pending(EntityEntry Entry, AuditAction Action, string? Details);

    /// <summary>Вызывается до сохранения: фиксирует, что и как меняется (исходные значения берутся из базы).</summary>
    public static async Task<List<Pending>> CollectAsync(DbContext db, CancellationToken ct)
    {
        var result = new List<Pending>();
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && Tracked.Contains(e.Entity.GetType()))
            .ToList();
        var newDocument = entries.Any(e => e.State == EntityState.Added && Documents.Contains(e.Entity.GetType()));

        foreach (var e in entries)
        {
            // Кассовые операции внутри документа (оплата продажи, закупа) — часть документа
            if (e.Entity is CashTransaction t && t.Kind is CashOperationKind.Sale or CashOperationKind.SaleReturn) continue;
            if (e.Entity is CashTransaction && newDocument) continue;

            switch (e.State)
            {
                case EntityState.Added:
                    result.Add(new(e, AuditAction.Create, Summary(e.Entity)));
                    break;
                case EntityState.Deleted:
                    result.Add(new(e, AuditAction.Delete, Summary(e.Entity)));
                    break;
                case EntityState.Modified:
                    // Изменились только служебные поля (остаток кассы и т. п.) — без запроса к базе
                    if (e.Properties.All(p => !p.IsModified || Ignored.Contains(p.Metadata.Name))) break;
                    var original = await e.GetDatabaseValuesAsync(ct).ConfigureAwait(false) ?? e.OriginalValues;
                    var (action, details) = Diff(e, original);
                    if (details is not null) result.Add(new(e, action, details));
                    break;
            }
        }
        return result;
    }

    /// <summary>Вызывается после сохранения (ключи новых записей уже известны).</summary>
    public static IEnumerable<AuditLog> Build(IEnumerable<Pending> pending)
    {
        var now = DateTime.Now;
        var machine = Environment.MachineName;
        foreach (var p in pending)
        {
            yield return new AuditLog
            {
                Date = now,
                UserId = AuditContext.UserId,
                UserName = AuditContext.UserName,
                Machine = machine,
                Action = p.Action,
                EntityType = p.Entry.Entity.GetType().Name,
                EntityId = KeyOf(p.Entry),
                EntityName = Truncate(NameOf(p.Entry.Entity), 300),
                Details = Truncate(p.Details, 4000)
            };
        }
    }

    private static (AuditAction, string?) Diff(EntityEntry e, PropertyValues original)
    {
        var action = AuditAction.Update;
        var sb = new StringBuilder();
        foreach (var prop in e.Properties)
        {
            var name = prop.Metadata.Name;
            if (Ignored.Contains(name) || prop.Metadata.IsShadowProperty()) continue;
            var before = original[prop.Metadata];
            var after = prop.CurrentValue;
            if (Same(before, after)) continue;

            if (name == "IsDeleted")
            {
                action = after is true ? AuditAction.Archive : AuditAction.Restore;
                continue;
            }
            if (sb.Length > 0) sb.Append("; ");
            var field = FieldNames.GetValueOrDefault(name, name);
            if (name == "PasswordHash") sb.Append("Пароль изменён");
            else if (after is byte[] || before is byte[]) sb.Append(field).Append(after is null ? ": удалено" : ": изменено");
            else sb.Append(field).Append(": ").Append(Format(before)).Append(" → ").Append(Format(after));
        }
        if (action != AuditAction.Update) return (action, sb.Length > 0 ? sb.ToString() : "");
        return (action, sb.Length > 0 ? sb.ToString() : null);
    }

    private static bool Same(object? a, object? b)
    {
        if (a is string { Length: 0 }) a = null;
        if (b is string { Length: 0 }) b = null;
        if (a is byte[] x && b is byte[] y) return x.AsSpan().SequenceEqual(y);
        return Equals(a, b);
    }

    private static string Format(object? v) => v switch
    {
        null => "—",
        string { Length: 0 } => "—",
        bool b => b ? "да" : "нет",
        decimal d => d.ToString("0.###", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
        CashDirection.Income => "Приход",
        CashDirection.Expense => "Расход",
        UserRole.Admin => "Администратор",
        UserRole.Manager => "Менеджер",
        UserRole.Cashier => "Кассир",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    private static string? Summary(object entity) => entity switch
    {
        Product p => $"Цена продажи: {Format(p.SalePrice)}; закупочная: {Format(p.PurchasePrice)}",
        Sale s => $"Сумма: {Format(s.Total)}",
        SaleReturn r => $"Сумма возврата: {Format(r.Total)}",
        Purchase p => $"Сумма: {Format(p.Total)}; оплачено: {Format(p.PaidAmount)}",
        InventoryCount i => $"Расхождение: {Format(i.DifferenceAmount)}",
        CashTransaction t => $"{Format(t.Direction)} {Format(t.Amount)}; {t.Category}" + (string.IsNullOrWhiteSpace(t.Comment) ? "" : $"; {t.Comment}"),
        User u => $"Логин: {u.Login}; роль: {Format(u.Role)}",
        _ => null
    };

    private static string? NameOf(object entity) => entity switch
    {
        ICatalogEntity c => c.Name,
        Sale s => s.Number,
        SaleReturn r => r.Number,
        Purchase p => p.Number,
        StockTransfer t => t.Number,
        InventoryCount i => i.Number,
        CashTransaction t => t.DocumentNumber ?? t.Category,
        User u => string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName,
        CompanySettings c => c.Name,
        ProductPhoto => "Фото товара",
        _ => null
    };

    private static int? KeyOf(EntityEntry e) =>
        e.Metadata.FindPrimaryKey()?.Properties is [var key] && e.Property(key.Name).CurrentValue is int id ? id : null;

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
