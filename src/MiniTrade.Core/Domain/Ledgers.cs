namespace MiniTrade.Core.Domain;

/// <summary>Текущий остаток товара на складе со средней себестоимостью.</summary>
public class StockBalance : Entity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public decimal Quantity { get; set; }
    public decimal AvgCost { get; set; }
}

/// <summary>
/// Партия товара на складе: остаток конкретного прихода по его цене. Ведётся при любом методе
/// себестоимости, чтобы переключение FIFO ↔ средняя работало без пересчёта истории.
/// </summary>
public class StockLot : Entity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    /// <summary>Дата прихода — порядок списания по FIFO.</summary>
    public DateTime Date { get; set; }
    public decimal UnitCost { get; set; }
    public decimal InitialQuantity { get; set; }
    /// <summary>Остаток партии.</summary>
    public decimal Quantity { get; set; }
    /// <summary>Документ-источник партии (0 — начальный остаток).</summary>
    public StockMovementType DocumentType { get; set; }
    public int DocumentId { get; set; }
}

/// <summary>Сколько из партии списано документом расхода — чтобы при отмене документа вернуть товар в ту же партию.</summary>
public class StockLotUsage : Entity
{
    public int LotId { get; set; }
    public StockLot? Lot { get; set; }
    public decimal Quantity { get; set; }
    public StockMovementType DocumentType { get; set; }
    public int DocumentId { get; set; }
}

public enum StockMovementType
{
    Purchase = 1,
    Sale = 2,
    SaleReturn = 3,
    TransferOut = 4,
    TransferIn = 5,
    InventoryAdjust = 6,
    PurchaseReturn = 7,
    /// <summary>Списание материалов в производство.</summary>
    ProductionConsume = 8,
    /// <summary>Выпуск готовой продукции.</summary>
    ProductionOutput = 9,
    /// <summary>Списание (порча, брак и т.п.).</summary>
    WriteOff = 10
}

/// <summary>Журнал движения товаров (источник истины для аудита остатков).</summary>
public class StockMovement : Entity
{
    public DateTime Date { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    /// <summary>Количество со знаком: + приход, − расход.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public StockMovementType Type { get; set; }
    public int DocumentId { get; set; }
    public string DocumentNumber { get; set; } = "";
}

public enum CashDirection
{
    Income = 1,
    Expense = 2
}

public enum CashOperationKind
{
    Manual = 0,
    Sale = 1,
    SaleReturn = 2,
    SupplierPayment = 3,
    TransferIn = 4,
    TransferOut = 5,
    /// <summary>Платёж контрагенту или от контрагента (не по документу продажи).</summary>
    CounterpartyPayment = 6,
    /// <summary>Деньги, возвращённые поставщиком при возврате товара.</summary>
    PurchaseReturn = 7,
    /// <summary>Начисление контрагенту без кассы: меняет только взаиморасчёты (Expense — контрагент должен нам, Income — мы должны ему).</summary>
    CounterpartyAccrual = 8
}

/// <summary>Операция по кассе. Сумма всегда положительная, направление задаётся Direction.</summary>
public class CashTransaction : Entity
{
    public DateTime Date { get; set; }
    /// <summary>Касса операции; null — начисление контрагенту (деньги не двигаются).</summary>
    public int? CashRegisterId { get; set; }
    public CashRegister? CashRegister { get; set; }
    public CashDirection Direction { get; set; }
    public CashOperationKind Kind { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Comment { get; set; }
    public int? DocumentId { get; set; }
    public string? DocumentNumber { get; set; }
    public int? UserId { get; set; }
    /// <summary>Контрагент операции (платёж контрагенту или от него).</summary>
    public int? CounterpartyId { get; set; }
    public Counterparty? Counterparty { get; set; }
    public string? CounterpartyName => Counterparty?.Name;
    public decimal SignedAmount => Direction == CashDirection.Income ? Amount : -Amount;
}

/// <summary>Счётчик нумерации документов.</summary>
public class DocumentSequence
{
    public string Key { get; set; } = "";
    public int Value { get; set; }
}
