namespace MiniTrade.Core.Domain;

/// <summary>Технологическая карта (рецептура): из каких материалов и в каком количестве выпускается продукция.</summary>
public class Recipe : Entity
{
    public string Name { get; set; } = "";
    /// <summary>Готовая продукция.</summary>
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>Сколько продукции получается из указанного набора материалов.</summary>
    public decimal OutputQuantity { get; set; } = 1;
    public string? Comment { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RecipeItem> Items { get; set; } = new();
}

/// <summary>Материал (сырьё) техкарты: расход на <see cref="Recipe.OutputQuantity"/> единиц продукции.</summary>
public class RecipeItem : Entity
{
    public int RecipeId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>
/// Документ производства: списание материалов со склада и оприходование готовой продукции.
/// Себестоимость единицы = (стоимость материалов + доп. расходы) / количество.
/// </summary>
public class ProductionOrder : Entity
{
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public int? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal MaterialsCost { get; set; }
    /// <summary>Дополнительные расходы (работа, упаковка и т. п.), включаемые в себестоимость.</summary>
    public decimal ExtraCost { get; set; }
    public decimal TotalCost { get; set; }
    public string? Comment { get; set; }
    public int? UserId { get; set; }
    public List<ProductionItem> Items { get; set; } = new();
}

/// <summary>Списанный материал документа производства.</summary>
public class ProductionItem : Entity
{
    public int ProductionOrderId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
}
