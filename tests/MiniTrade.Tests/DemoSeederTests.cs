using Microsoft.EntityFrameworkCore;
using MiniTrade.Core.Data;

namespace MiniTrade.Tests;

public class DemoSeederTests
{
    [Theory]
    [InlineData(DemoIndustry.Manufacturing)]
    [InlineData(DemoIndustry.Services)]
    [InlineData(DemoIndustry.Grocery)]
    public async Task Demo_profile_fills_year_of_consistent_data(DemoIndustry industry)
    {
        using var t = new TestDb();
        await DemoCompanySeeder.RunAsync(t, t.Session, industry, seed: 42);

        Assert.True(t.Query(db => db.Sales.Count()) > 300);
        await Integrity.AssertConsistentAsync(t, industry.ToString());

        if (industry == DemoIndustry.Manufacturing)
        {
            Assert.Equal(DemoIndustries.Get(industry).Recipes!.Length, t.Query(db => db.Recipes.Count()));
            Assert.True(t.Query(db => db.ProductionOrders.Count()) > 50);
        }
        if (industry == DemoIndustry.Services)
        {
            Assert.True(t.Query(db => db.SaleItems.Count(i => i.Product!.IsService)) > 100);
            Assert.Equal(0, t.Query(db => db.PurchaseItems.Count(i => i.Product!.IsService)));
        }
    }
}
