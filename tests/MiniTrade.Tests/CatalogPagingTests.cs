using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

/// <summary>Постраничная выборка справочников, поиск в базе, статьи доходов/расходов, переводимые бизнес-ошибки.</summary>
public class CatalogPagingTests
{
    [Fact]
    public async Task Products_are_paged_in_database_ordered_by_name()
    {
        using var t = new TestDb();
        for (var i = 1; i <= 23; i++)
            await t.Catalog.SaveAsync(new Product { Name = $"Товар {i:00}", SalePrice = i });

        var p1 = await t.Catalog.GetPageAsync<Product>(null, false, 0, 10);
        var p3 = await t.Catalog.GetPageAsync<Product>(null, false, 20, 10);

        Assert.Equal(23, p1.TotalCount);
        Assert.Equal(10, p1.Items.Count);
        Assert.Equal("Товар 01", p1.Items[0].Name);
        Assert.Equal(["Товар 21", "Товар 22", "Товар 23"], p3.Items.Select(p => p.Name));
    }

    [Fact]
    public async Task Search_finds_cyrillic_in_any_case_and_by_sku_and_barcode()
    {
        using var t = new TestDb();
        await t.Catalog.SaveAsync(new Product { Name = "Кола 1.5 л", Sku = "cola-15", Barcode = "4870001112223" });
        await t.Catalog.SaveAsync(new Product { Name = "Сахар", Sku = "SUG-1" });

        Assert.Equal("Кола 1.5 л", Assert.Single((await t.Catalog.GetPageAsync<Product>("кола", false, 0, 50)).Items).Name);
        Assert.Single((await t.Catalog.GetPageAsync<Product>("КОЛА", false, 0, 50)).Items);
        Assert.Single((await t.Catalog.GetPageAsync<Product>("COLA", false, 0, 50)).Items);
        Assert.Single((await t.Catalog.GetPageAsync<Product>("1112", false, 0, 50)).Items);
        Assert.Equal("Сахар", Assert.Single((await t.Catalog.GetPageAsync<Product>("sug", false, 0, 50)).Items).Name);
        Assert.Equal(0, (await t.Catalog.GetPageAsync<Product>("нет такого", false, 0, 50)).TotalCount);
    }

    [Fact]
    public async Task Archived_records_are_hidden_unless_requested()
    {
        using var t = new TestDb();
        await t.Catalog.SaveAsync(new Counterparty { Name = "Активный" });
        await t.Catalog.SaveAsync(new Counterparty { Name = "Архивный", IsActive = false });

        Assert.Equal(1, (await t.Catalog.GetPageAsync<Counterparty>(null, false, 0, 50)).TotalCount);
        Assert.Equal(2, (await t.Catalog.GetPageAsync<Counterparty>(null, true, 0, 50)).TotalCount);
    }

    [Fact]
    public async Task Seed_fills_income_and_expense_articles()
    {
        using var t = new TestDb();
        var income = await t.Finance.GetArticlesAsync(CashDirection.Income);
        var expense = await t.Finance.GetArticlesAsync(CashDirection.Expense);
        Assert.Contains(income, a => a.Name == "Начальный остаток");
        Assert.Contains(expense, a => a.Name == "Аренда");
        Assert.DoesNotContain(expense, a => a.Direction != CashDirection.Expense);
    }

    [Fact]
    public async Task Business_error_with_values_keeps_format_for_translation()
    {
        using var t = new TestDb();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            t.Finance.AddOperationAsync(t.CashId, CashDirection.Expense, "Аренда", 500m, null));
        Assert.Equal("Недостаточно средств в кассе «{0}»: остаток {1}, требуется {2}.", ex.Format);
        Assert.Equal(3, ex.Args.Length);
        Assert.Contains("Основная касса", ex.Message);
    }
}
