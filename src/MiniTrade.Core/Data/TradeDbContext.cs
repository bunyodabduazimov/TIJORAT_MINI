using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Data;

/// <summary>База торговых данных: локальная SQLite (по умолчанию) или сервер MySQL (см. <see cref="MySqlTradeDbContext"/>).</summary>
public class TradeDbContext : DbContext
{
    public TradeDbContext(DbContextOptions<TradeDbContext> options) : base(options) { }
    protected TradeDbContext(DbContextOptions options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Counterparty> Counterparties => Set<Counterparty>();
    public DbSet<CounterpartyGroup> CounterpartyGroups => Set<CounterpartyGroup>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<MeasureUnit> MeasureUnits => Set<MeasureUnit>();
    public DbSet<ScaleDevice> Scales => Set<ScaleDevice>();
    public DbSet<CashRegister> CashRegisters => Set<CashRegister>();
    public DbSet<FinanceArticle> FinanceArticles => Set<FinanceArticle>();
    public DbSet<ProductPhoto> ProductPhotos => Set<ProductPhoto>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<CashTransaction> CashTransactions => Set<CashTransaction>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<SaleReturnItem> SaleReturnItems => Set<SaleReturnItem>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<StockLot> StockLots => Set<StockLot>();
    public DbSet<StockLotUsage> StockLotUsages => Set<StockLotUsage>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnItem> PurchaseReturnItems => Set<PurchaseReturnItem>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<InventoryCount> InventoryCounts => Set<InventoryCount>();
    public DbSet<StockWriteOff> StockWriteOffs => Set<StockWriteOff>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeItem> RecipeItems => Set<RecipeItem>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionItem> ProductionItems => Set<ProductionItem>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Отключить журнал действий для этого контекста (служебные операции, тесты).</summary>
    public bool AuditEnabled { get; set; } = true;

    /// <summary>Записи «Создание», сделанные этим контекстом (см. AuditTracker.CollectAsync).</summary>
    private readonly Dictionary<object, AuditLog> _createdLogs = new(ReferenceEqualityComparer.Instance);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    /// <summary>Сохраняет изменения и записывает их в журнал действий (<see cref="AuditLog"/>).</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        TouchProducts();
        if (!AuditEnabled) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct).ConfigureAwait(false);
        var pending = await AuditTracker.CollectAsync(this, _createdLogs, ct).ConfigureAwait(false);
        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            // Записи журнала создаются после сохранения — ключи новых объектов уже известны
            foreach (var (entity, log) in AuditTracker.Build(pending).ToList())
            {
                AuditLogs.Add(log);
                if (log.Action == AuditAction.Create) _createdLogs[entity] = log;
            }
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct).ConfigureAwait(false);
        }
        return count;
    }

    private static readonly string[] ScaleFields =
        [nameof(Product.Name), nameof(Product.Barcode), nameof(Product.SalePrice), nameof(Product.IsWeighted), nameof(Product.IsActive), nameof(Product.IsDeleted)];

    /// <summary>Дата изменения товара — при создании или правке полей, которые уходят в весы.</summary>
    private void TouchProducts()
    {
        var now = DateTime.Now;
        foreach (var e in ChangeTracker.Entries<Product>())
        {
            if (e.State == EntityState.Added
                // Update() целиком помечает все поля — дату тогда выставляет вызывающий код (CatalogService)
                || (e.State == EntityState.Modified && !e.Property(nameof(Product.UpdatedAt)).IsModified
                    && ScaleFields.Any(f => e.Property(f).IsModified)))
                e.Entity.UpdatedAt = now;
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Справочники
        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name);
        });
        b.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Property(x => x.Unit).HasMaxLength(16);
            e.HasIndex(x => x.Sku);
            e.HasIndex(x => x.Barcode);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.Thumbnail);
        });
        b.Entity<ProductPhoto>(e =>
        {
            e.HasKey(x => x.ProductId);
            e.HasOne<Product>().WithOne().HasForeignKey<ProductPhoto>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CounterpartyGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
        });
        b.Entity<Counterparty>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.Name);
            e.Ignore(x => x.Balance);
            e.Ignore(x => x.BalanceSign);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Warehouse>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Ignore(x => x.StockCost);
            e.Ignore(x => x.StockSaleValue);
        });
        b.Entity<MeasureUnit>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(16).IsRequired();
            e.Property(x => x.Description).HasMaxLength(100);
        });
        b.Entity<ScaleDevice>(e =>
        {
            e.ToTable("Scales");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Ip).HasMaxLength(64).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(260);
            e.Ignore(x => x.IsFileBased);
        });
        b.Entity<CashRegister>(e => e.Property(x => x.Name).HasMaxLength(200).IsRequired());
        b.Entity<FinanceArticle>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => new { x.Direction, x.Name });
        });

        // Остатки и журналы
        b.Entity<StockBalance>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.WarehouseId }).IsUnique();
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockMovement>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.WarehouseId, x.Date });
            e.HasIndex(x => new { x.Type, x.DocumentId });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CashTransaction>(e =>
        {
            e.HasIndex(x => x.Date);
            e.Property(x => x.Category).HasMaxLength(100);
            e.HasOne(x => x.CashRegister).WithMany().HasForeignKey(x => x.CashRegisterId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.SignedAmount);
            e.Ignore(x => x.CounterpartyName);
            // Контрагент с платежами не удаляется физически — уходит в архив (см. CatalogService.DeleteAsync)
            e.HasOne(x => x.Counterparty).WithMany().HasForeignKey(x => x.CounterpartyId).OnDelete(DeleteBehavior.Restrict);
        });

        // Документы
        b.Entity<Sale>(e =>
        {
            e.Ignore(x => x.Debt);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CashRegister).WithMany().HasForeignKey(x => x.CashRegisterId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne(x => x.Sale).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SaleItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<SaleReturn>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CashRegister).WithMany().HasForeignKey(x => x.CashRegisterId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne(x => x.SaleReturn).HasForeignKey(x => x.SaleReturnId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SaleReturnItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<Purchase>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.Ignore(x => x.Debt);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne(x => x.Purchase).HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PurchaseItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<StockLot>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.WarehouseId, x.Date });
            e.HasIndex(x => new { x.DocumentType, x.DocumentId });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<StockLotUsage>(e =>
        {
            e.HasIndex(x => new { x.DocumentType, x.DocumentId });
            e.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PurchaseReturn>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasOne(x => x.Purchase).WithMany().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CashRegister).WithMany().HasForeignKey(x => x.CashRegisterId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne(x => x.PurchaseReturn).HasForeignKey(x => x.PurchaseReturnId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PurchaseReturnItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<StockTransfer>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne(x => x.FromWarehouse).WithMany().HasForeignKey(x => x.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToWarehouse).WithMany().HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.StockTransferId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<StockTransferItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<InventoryCount>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InventoryCountId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<InventoryCountItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<StockWriteOff>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.StockWriteOffId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<StockWriteOffItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));

        // Производство
        b.Entity<Recipe>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(500);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RecipeItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<ProductionOrder>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.Property(x => x.Comment).HasMaxLength(500);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Recipe).WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductionItem>(e => e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict));

        // Система
        b.Entity<User>(e =>
        {
            e.Property(x => x.Login).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Login).IsUnique();
            e.Property(x => x.Phone).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.LastLoginMachine).HasMaxLength(100);
            e.Ignore(x => x.IsAdmin);
            e.HasOne(x => x.AccessRole).WithMany().HasForeignKey(x => x.AccessRoleId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AccessRole>(e =>
        {
            e.ToTable("Roles");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Permissions).HasMaxLength(4000);
        });
        b.Entity<DocumentSequence>(e => e.HasKey(x => x.Key));
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.Date);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.UserName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Machine).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityName).HasMaxLength(300);
            e.Property(x => x.Details).HasMaxLength(4000);
        });
    }
}
