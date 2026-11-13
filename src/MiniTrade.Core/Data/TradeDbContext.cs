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
    public DbSet<User> Users => Set<User>();
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Отключить журнал действий для этого контекста (служебные операции, тесты).</summary>
    public bool AuditEnabled { get; set; } = true;

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    /// <summary>Сохраняет изменения и записывает их в журнал действий (<see cref="AuditLog"/>).</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        if (!AuditEnabled) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct).ConfigureAwait(false);
        var pending = await AuditTracker.CollectAsync(this, ct).ConfigureAwait(false);
        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            // Записи журнала создаются после сохранения — ключи новых объектов уже известны
            AuditLogs.AddRange(AuditTracker.Build(pending).ToList());
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct).ConfigureAwait(false);
        }
        return count;
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
        b.Entity<Warehouse>(e => e.Property(x => x.Name).HasMaxLength(200).IsRequired());
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

        // Система
        b.Entity<User>(e =>
        {
            e.Property(x => x.Login).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Login).IsUnique();
            e.Property(x => x.Phone).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.LastLoginMachine).HasMaxLength(100);
            e.Ignore(x => x.EffectivePermissions);
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
