using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using Xunit;

namespace MiniTrade.Tests;

public class AuditLogTests
{
    [Fact]
    public async Task Catalog_changes_are_logged_with_old_and_new_values()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Журнальный товар", 10m);
        p.SalePrice = 15m;
        await t.Catalog.SaveAsync(p);
        await t.Catalog.DeleteAsync<Product>(p.Id);

        var logs = t.Query(db => db.AuditLogs.Where(a => a.EntityType == nameof(Product) && a.EntityId == p.Id).OrderBy(a => a.Id).ToList());
        Assert.Equal(AuditAction.Create, logs[0].Action);
        Assert.Equal("Журнальный товар", logs[0].EntityName);
        var update = Assert.Single(logs, a => a.Action == AuditAction.Update);
        Assert.Contains("Цена продажи: 10 → 15", update.Details);
        Assert.Contains(logs, a => a.Action is AuditAction.Delete or AuditAction.Archive);
    }

    [Fact]
    public async Task Sale_is_logged_once_without_ledger_noise()
    {
        using var t = new TestDb();
        var p = await t.ProductAsync("Товар для чека", 20m);
        await t.Purchases.CreatePurchaseAsync(new PurchaseRequest((await t.SupplierAsync()).Id, t.WarehouseId, [new DocumentLine(p.Id, 5, 10m)]));
        var before = t.Query(db => db.AuditLogs.Count());

        var sale = await t.Sales.CreateSaleAsync(new SaleRequest(t.WarehouseId, t.CashId, [new DocumentLine(p.Id, 2, 20m)]));

        var added = t.Query(db => db.AuditLogs.OrderBy(a => a.Id).Skip(before).ToList());
        var log = Assert.Single(added);
        Assert.Equal(nameof(Sale), log.EntityType);
        Assert.Equal(AuditAction.Create, log.Action);
        Assert.Equal(sale.Id, log.EntityId);
    }
}
