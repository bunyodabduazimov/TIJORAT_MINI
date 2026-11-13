namespace MiniTrade.Core.Domain;

public class Sale : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int? CustomerId { get; set; }
    public Counterparty? Customer { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public int CashRegisterId { get; set; }
    public CashRegister? CashRegister { get; set; }
    public decimal Total { get; set; }
    /// <summary>Себестоимость проданного (по средней цене на момент продажи).</summary>
    public decimal CostTotal { get; set; }
    public decimal ReturnedTotal { get; set; }
    /// <summary>Оплачено при продаже; остаток (Total − PaidAmount) — долг покупателя.</summary>
    public decimal PaidAmount { get; set; }
    public string? Comment { get; set; }
    public int? UserId { get; set; }
    public List<SaleItem> Items { get; set; } = new();
    public decimal Debt => Total - PaidAmount;
}

public class SaleItem : Entity
{
    public int SaleId { get; set; }
    public Sale? Sale { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ReturnedQuantity { get; set; }
}

public class SaleReturn : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int SaleId { get; set; }
    public Sale? Sale { get; set; }
    public int CashRegisterId { get; set; }
    public CashRegister? CashRegister { get; set; }
    public decimal Total { get; set; }
    public decimal CostTotal { get; set; }
    public string? Reason { get; set; }
    public int? UserId { get; set; }
    public List<SaleReturnItem> Items { get; set; } = new();
}

public class SaleReturnItem : Entity
{
    public int SaleReturnId { get; set; }
    public SaleReturn? SaleReturn { get; set; }
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }
    public decimal UnitCost { get; set; }
}

public class Purchase : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int SupplierId { get; set; }
    public Counterparty? Supplier { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public decimal Total { get; set; }
    public decimal PaidAmount { get; set; }
    /// <summary>Возвращено поставщику (уже вычтено из Total).</summary>
    public decimal ReturnedTotal { get; set; }
    public string? Comment { get; set; }
    public int? UserId { get; set; }
    public List<PurchaseItem> Items { get; set; } = new();
    public decimal Debt => Total - PaidAmount;
}

public class PurchaseItem : Entity
{
    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }
    public decimal ReturnedQuantity { get; set; }
}

/// <summary>Возврат товара поставщику по закупке: уменьшает сумму закупки, излишек оплаты возвращается в кассу.</summary>
public class PurchaseReturn : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }
    /// <summary>Касса, в которую поставщик вернул деньги (если закупка была оплачена).</summary>
    public int? CashRegisterId { get; set; }
    public CashRegister? CashRegister { get; set; }
    public decimal Total { get; set; }
    /// <summary>Сколько денег вернул поставщик; остальное зачтено в счёт долга.</summary>
    public decimal RefundAmount { get; set; }
    public string? Reason { get; set; }
    public int? UserId { get; set; }
    public List<PurchaseReturnItem> Items { get; set; } = new();
}

public class PurchaseReturnItem : Entity
{
    public int PurchaseReturnId { get; set; }
    public PurchaseReturn? PurchaseReturn { get; set; }
    public int PurchaseItemId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }
}

public class StockTransfer : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int FromWarehouseId { get; set; }
    public Warehouse? FromWarehouse { get; set; }
    public int ToWarehouseId { get; set; }
    public Warehouse? ToWarehouse { get; set; }
    public string? Comment { get; set; }
    public List<StockTransferItem> Items { get; set; } = new();
}

public class StockTransferItem : Entity
{
    public int StockTransferId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public class InventoryCount : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public string? Comment { get; set; }
    /// <summary>Сумма расхождения по себестоимости (+ излишек, − недостача).</summary>
    public decimal DifferenceAmount { get; set; }
    public List<InventoryCountItem> Items { get; set; } = new();
}

public class InventoryCountItem : Entity
{
    public int InventoryCountId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal BookQuantity { get; set; }
    public decimal ActualQuantity { get; set; }
    public decimal Difference { get; set; }
    public decimal UnitCost { get; set; }
}
