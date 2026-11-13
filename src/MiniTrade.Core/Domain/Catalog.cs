namespace MiniTrade.Core.Domain;

/// <summary>Базовая сущность с целочисленным ключом.</summary>
public abstract class Entity
{
    public int Id { get; set; }
}

/// <summary>Справочная сущность, которую можно архивировать вместо удаления.</summary>
public interface ICatalogEntity
{
    int Id { get; set; }
    string Name { get; set; }
    bool IsActive { get; set; }
    /// <summary>Мягкое удаление: запись остаётся в базе (для документов), но скрыта из справочников.</summary>
    bool IsDeleted { get; set; }
}

public class Category : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Картинка категории (небольшая JPEG-миниатюра) — для плиток кассы.</summary>
    public byte[]? Image { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public override string ToString() => Name;
}

public class Product : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    /// <summary>Артикул.</summary>
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string Unit { get; set; } = "шт";
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    /// <summary>Закупочная цена по умолчанию.</summary>
    public decimal PurchasePrice { get; set; }
    /// <summary>Розничная цена продажи.</summary>
    public decimal SalePrice { get; set; }
    /// <summary>Минимальный остаток для уведомлений.</summary>
    public decimal MinStock { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }

    /// <summary>Миниатюра фото для списков (не хранится в таблице товаров — см. <see cref="ProductPhoto"/>).</summary>
    public byte[]? Thumbnail { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Sku) ? Name : $"{Name} ({Sku})";
}

/// <summary>
/// Фото товара (JPEG) и его миниатюра. Отдельная таблица: списки товаров не тянут картинки,
/// а резервная копия базы сохраняет фото вместе с данными.
/// </summary>
public class ProductPhoto
{
    public int ProductId { get; set; }
    public byte[] Data { get; set; } = [];
    public byte[] Thumbnail { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Группа контрагентов (Покупатели, Поставщики, Оптовики…) — задаёт пользователь.</summary>
public class CounterpartyGroup : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public override string ToString() => Name;
}

/// <summary>Контрагент: покупатель, поставщик или оба сразу — один справочник, разделение по группам.</summary>
public class Counterparty : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    public int? GroupId { get; set; }
    public CounterpartyGroup? Group { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    /// <summary>
    /// Взаиморасчёты (не хранится): &gt; 0 — контрагент должен нам, &lt; 0 — мы должны контрагенту.
    /// Заполняется при загрузке справочника.
    /// </summary>
    public decimal Balance { get; set; }
    public int BalanceSign => Math.Sign(Balance);
    public override string ToString() => Name;
}

public class Warehouse : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public override string ToString() => Name;
}

/// <summary>Статья дохода или расхода для ручных кассовых операций (Аренда, Зарплата…).</summary>
public class FinanceArticle : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    public CashDirection Direction { get; set; } = CashDirection.Expense;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public override string ToString() => Name;
}

public class CashRegister : Entity, ICatalogEntity
{
    public string Name { get; set; } = "";
    /// <summary>Текущий остаток (поддерживается сервисами атомарно вместе с журналом операций).</summary>
    public decimal Balance { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public override string ToString() => Name;
}
