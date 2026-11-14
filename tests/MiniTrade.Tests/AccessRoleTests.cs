using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;

namespace MiniTrade.Tests;

public class AccessRoleTests
{
    [Fact]
    public async Task Seed_creates_builtin_roles_and_assigns_admin()
    {
        using var t = new TestDb();
        var settings = new SettingsService(t, t.Session);
        var roles = await settings.GetRolesAsync();

        Assert.Equal(["Администратор", "Кассир", "Менеджер"], roles.Select(r => r.Name));
        Assert.True(roles[0].IsAdmin);
        var admin = Assert.Single(await settings.GetUsersAsync());
        Assert.Equal(roles[0].Id, admin.AccessRoleId);
        Assert.True(admin.Can(Perm.SettingsUsers));
    }

    [Fact]
    public void Role_grants_only_listed_actions()
    {
        var cashier = new User { Role = UserRole.Cashier, AccessRole = new AccessRole { Permissions = PermissionCatalog.DefaultCashier } };
        Assert.True(cashier.Can(Perm.SalesCreate));
        Assert.False(cashier.Can(Perm.SalesDelete));
        Assert.False(cashier.Can(Perm.WarehouseView));

        var manager = new User { AccessRole = new AccessRole { Permissions = PermissionCatalog.DefaultManager } };
        Assert.True(manager.Can(Perm.ProductionCancel));
        Assert.False(manager.Can(Perm.AuditView));
    }

    [Fact]
    public void Legacy_section_flags_convert_to_all_module_actions()
    {
        var keys = PermissionCatalog.FromLegacy(Permission.Sales | Permission.Warehouse).Split(',');
        Assert.Contains(Perm.SalesDelete, keys);
        Assert.Contains(Perm.WarehouseInventory, keys);
        Assert.DoesNotContain(Perm.FinanceView, keys);
    }

    [Fact]
    public void Old_module_rights_are_spread_to_tabs_on_upgrade()
    {
        var upgraded = PermissionCatalog.Upgrade("catalog.view,catalog.create,finance.view,finance.create,reports.view,sales.view")!;
        var set = new AccessRole { Permissions = upgraded }.PermissionSet();
        Assert.Contains("catalog.products.view", set);
        Assert.Contains("catalog.cashregisters.create", set);
        Assert.DoesNotContain("catalog.products.delete", set);
        Assert.DoesNotContain("catalog.create", set);
        Assert.Contains(Perm.FinanceIncomeCreate, set);
        Assert.Contains(Perm.ReportsProfit, set);
        Assert.Contains(Perm.SalesHistory, set);
        Assert.Null(PermissionCatalog.Upgrade(upgraded));
    }

    [Fact]
    public async Task Custom_role_is_saved_assigned_and_protected_from_delete()
    {
        using var t = new TestDb();
        var settings = new SettingsService(t, t.Session);
        var role = await settings.SaveRoleAsync(new AccessRole { Name = "Кладовщик" },
            [Perm.WarehouseView, Perm.WarehouseTransfer, "unknown.key"]);
        // Доступ к модулю не хранится — он следует из любого права модуля
        Assert.Equal(["warehouse.transfer"], role.PermissionSet());
        Assert.True(role.Has(Perm.WarehouseView));
        Assert.False(role.Has(Perm.SalesView));
        Assert.Null(PermissionCatalog.Upgrade(role.Permissions)); // сохранённая роль уже в новом формате

        await settings.SaveUserAsync(new User { Login = "Store", FullName = "Склад", AccessRoleId = role.Id }, "1234");
        var user = (await settings.GetUsersAsync()).Single(u => u.Login == "store");
        Assert.Equal("Кладовщик", user.AccessRole!.Name);
        Assert.True(user.Can(Perm.WarehouseTransfer));
        Assert.False(user.Can(Perm.WarehouseInventory));

        await Assert.ThrowsAsync<BusinessException>(() => settings.DeleteRoleAsync(role.Id));
        var admin = (await settings.GetRolesAsync()).Single(r => r.IsAdmin);
        await Assert.ThrowsAsync<BusinessException>(() => settings.DeleteRoleAsync(admin.Id));
        await Assert.ThrowsAsync<BusinessException>(() => settings.SaveRoleAsync(new AccessRole { Name = "Кладовщик" }, []));
    }
}
