using MiniTrade.Core.Common;
using MiniTrade.Core.Data;
using MiniTrade.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MiniTrade.Core.Services;

/// <summary>Платёж контрагенту или от контрагента.</summary>
public sealed record CounterpartyPaymentRequest(CashDirection Direction, int CashRegisterId, int CounterpartyId,
    decimal Amount, string? Article = null, string? Comment = null, DateTime? Date = null);

/// <summary>
/// Фильтр журнала кассовых операций. Search ищет по статье, комментарию и номеру документа.
/// FinanceOnly — только операции раздела «Финансы» (приход/расход, перемещение, платежи и начисления контрагентам),
/// без продаж, закупок и возвратов. LatestAdded — последние добавленные сверху (по порядку ввода), период не учитывается.
/// </summary>
public sealed record CashOperationFilter(DateTime From, DateTime To, int? CashRegisterId = null, CashDirection? Direction = null,
    int? CounterpartyId = null, string? Search = null, CashOperationKind[]? Kinds = null,
    bool FinanceOnly = false, bool LatestAdded = false);

/// <summary>Страница журнала и итоги по всему отфильтрованному периоду (без перемещений между кассами).</summary>
public sealed record CashOperationsPage(IReadOnlyList<CashTransaction> Items, int TotalCount, decimal Income, decimal Expense);

/// <summary>Финансы: кассы, ручные доходы/расходы, перемещение денег между кассами.</summary>
public class FinanceService(IDbContextFactory<TradeDbContext> factory, UserSession session)
{
    /// <summary>Стартовые статьи — заносятся в справочник «Статьи доходов и расходов» при создании базы.</summary>
    public static readonly string[] DefaultIncomeArticles = ["Прочий доход", "Внесение наличных", "Оплата от клиента", "Начальный остаток"];
    public static readonly string[] DefaultExpenseArticles = ["Аренда", "Зарплата", "Коммунальные услуги", "Транспорт", "Налоги", "Реклама", "Изъятие наличных", "Прочий расход"];

    /// <summary>Активные статьи нужного направления для выбора в операции.</summary>
    public async Task<List<FinanceArticle>> GetArticlesAsync(CashDirection direction, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.FinanceArticles.AsNoTracking().Where(a => a.IsActive && a.Direction == direction).ToListAsync(ct))
            .OrderBy(a => a.Name).ToList();
    }

    public async Task<List<CashRegister>> GetCashRegistersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return Access.Scope(await db.CashRegisters.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(ct));
    }

    public async Task<CashTransaction> AddOperationAsync(int cashRegisterId, CashDirection direction, string category, decimal amount,
        string? comment, DateTime? date = null, CancellationToken ct = default)
    {
        Access.Demand(direction == CashDirection.Income ? Perm.FinanceIncomeCreate : Perm.FinanceExpenseCreate);
        if (string.IsNullOrWhiteSpace(category)) throw new BusinessException("Укажите статью операции.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Cash, ct);
        var op = await CashLedger.PostAsync(db, cashRegisterId, direction, CashOperationKind.Manual, category.Trim(), amount, comment,
            date ?? DateTime.Now, null, number, session.UserId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return op;
    }

    /// <summary>
    /// Можно ли изменить/удалить операцию из журнала. Только ручные приходы/расходы и платежи контрагентам,
    /// не погашавшие закупки: остальные созданы документами (продажа, возврат, закупка, перемещение) и меняются через них.
    /// </summary>
    public static bool CanModify(CashTransaction t) => t.Kind is CashOperationKind.Manual or CashOperationKind.CounterpartyPayment or CashOperationKind.CounterpartyAccrual;

    /// <summary>Подраздел прав операции: finance.income / expense / payment / accrual / transfer; null — операция документа.</summary>
    public static string? PermGroupOf(CashTransaction t) => t.Kind switch
    {
        CashOperationKind.Manual => t.Direction == CashDirection.Income ? "finance.income" : "finance.expense",
        CashOperationKind.CounterpartyPayment or CashOperationKind.SupplierPayment => "finance.payment",
        CashOperationKind.CounterpartyAccrual => "finance.accrual",
        CashOperationKind.TransferIn or CashOperationKind.TransferOut => "finance.transfer",
        _ => null
    };

    /// <summary>Разрешено ли текущему пользователю действие (view/create/edit/delete) с операцией.</summary>
    public static bool CanDo(CashTransaction t, string action) =>
        PermGroupOf(t) is { } g ? Access.Can($"{g}.{action}") : action == "view" && Access.Can(Perm.FinanceView);

    /// <summary>Изменение ручной операции: касса, статья, сумма, комментарий, контрагент. Остатки касс пересчитываются.</summary>
    public async Task UpdateOperationAsync(int id, int? cashRegisterId, string category, decimal amount, string? comment,
        int? counterpartyId = null, DateTime? date = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new BusinessException("Укажите статью операции.");
        amount = Money.Round(amount);
        if (amount <= 0) throw new BusinessException("Сумма операции должна быть больше нуля.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await LoadModifiableAsync(db, id, ct);
        Access.Demand($"{PermGroupOf(op)}.edit");
        if (op.Kind is CashOperationKind.CounterpartyPayment or CashOperationKind.CounterpartyAccrual && counterpartyId is null)
            throw new BusinessException("Выберите контрагента.");

        if (op.Kind == CashOperationKind.CounterpartyAccrual)
        {
            // Начисление: касса не участвует
            op.Category = category.Trim();
            op.Amount = amount;
            op.Comment = comment;
            op.CounterpartyId = counterpartyId;
            if (date is { } d0) op.Date = d0;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }
        if (cashRegisterId is null) throw new BusinessException("Выберите кассу.");

        // Снимаем старую сумму со старой кассы и проводим новую по новой кассе
        var oldCash = await db.CashRegisters.FindAsync([op.CashRegisterId], ct) ?? throw new BusinessException("Касса не найдена.");
        oldCash.Balance = Money.Round(oldCash.Balance - op.SignedAmount);
        var newCash = await db.CashRegisters.FindAsync([cashRegisterId], ct) ?? throw new BusinessException("Касса не найдена.");
        op.CashRegisterId = cashRegisterId;
        op.Category = category.Trim();
        op.Amount = amount;
        op.Comment = comment;
        if (op.Kind == CashOperationKind.CounterpartyPayment) op.CounterpartyId = counterpartyId;
        if (date is { } d1) op.Date = d1;
        newCash.Balance = Money.Round(newCash.Balance + op.SignedAmount);
        EnsureNotNegative(oldCash);
        EnsureNotNegative(newCash);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Удаление ручной операции: её сумма возвращается в остаток кассы.</summary>
    public async Task DeleteOperationAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await LoadModifiableAsync(db, id, ct);
        Access.Demand($"{PermGroupOf(op)}.delete");
        if (op.CashRegisterId is not null)
        {
            var cash = await db.CashRegisters.FindAsync([op.CashRegisterId], ct) ?? throw new BusinessException("Касса не найдена.");
            cash.Balance = Money.Round(cash.Balance - op.SignedAmount);
            EnsureNotNegative(cash);
        }
        db.CashTransactions.Remove(op);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<CashTransaction> LoadModifiableAsync(TradeDbContext db, int id, CancellationToken ct)
    {
        var op = await db.CashTransactions.FindAsync([id], ct) ?? throw new BusinessException("Операция не найдена.");
        if (!CanModify(op))
            throw new BusinessException("Операция {0} создана документом — изменить или удалить её можно только через документ.", op.DocumentNumber ?? "");
        return op;
    }

    private static void EnsureNotNegative(CashRegister cash)
    {
        if (cash.Balance < 0)
            throw new BusinessException("Недостаточно средств в кассе «{0}»: остаток станет {1}.", cash.Name, Money.Format(cash.Balance));
    }

    public async Task TransferAsync(int fromCashId, int toCashId, decimal amount, string? comment, DateTime? date = null, CancellationToken ct = default)
    {
        Access.Demand(Perm.FinanceTransferCreate);
        if (fromCashId == toCashId) throw new BusinessException("Кассы должны различаться.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Cash, ct);
        var now = date ?? DateTime.Now;
        await CashLedger.PostAsync(db, fromCashId, CashDirection.Expense, CashOperationKind.TransferOut, "Перемещение между кассами", amount, comment, now, null, number, session.UserId, ct);
        await CashLedger.PostAsync(db, toCashId, CashDirection.Income, CashOperationKind.TransferIn, "Перемещение между кассами", amount, comment, now, null, number, session.UserId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Платёж контрагенту (Expense) или от контрагента (Income). Указывается покупатель или поставщик.
    /// Платёж поставщику гасит его долг по закупкам — начиная с самых старых.
    /// </summary>
    public async Task<CashTransaction> CounterpartyPaymentAsync(CounterpartyPaymentRequest r, CancellationToken ct = default)
    {
        // Платёж из «Финансов» или оплата закупки из раздела «Закуп»
        if (!Access.Can(Perm.PurchasesPay)) Access.Demand(Perm.FinancePaymentCreate);
        if (r.CounterpartyId <= 0) throw new BusinessException("Выберите контрагента.");
        var amount = Money.Round(r.Amount);
        if (amount <= 0) throw new BusinessException("Сумма операции должна быть больше нуля.");
        var article = string.IsNullOrWhiteSpace(r.Article)
            ? (r.Direction == CashDirection.Expense ? "Оплата контрагенту" : "Оплата от контрагента") : r.Article.Trim();

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, DocumentNumbers.Cash, ct);
        var supplierDebt = r.Direction == CashDirection.Expense
            ? (await db.Purchases.Where(p => p.SupplierId == r.CounterpartyId).ToListAsync(ct)).Where(p => p.Debt > 0).ToList()
            : [];
        var paysSupplierDebt = supplierDebt.Count > 0;
        var op = await CashLedger.PostAsync(db, r.CashRegisterId, r.Direction,
            paysSupplierDebt ? CashOperationKind.SupplierPayment : CashOperationKind.CounterpartyPayment,
            article, amount, r.Comment, r.Date ?? DateTime.Now, null, number, session.UserId, ct);
        op.CounterpartyId = r.CounterpartyId;

        if (paysSupplierDebt)
        {
            // Распределяем оплату по неоплаченным закупкам (FIFO); остаток сверх долга — аванс
            var purchases = supplierDebt.OrderBy(p => p.Date).ThenBy(p => p.Id);
            var left = amount;
            foreach (var p in purchases)
            {
                if (left <= 0) break;
                var part = Math.Min(left, p.Debt);
                p.PaidAmount = Money.Round(p.PaidAmount + part);
                left -= part;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return op;
    }

    /// <summary>
    /// Начисление контрагенту без участия кассы (услуги, бонусы, корректировка долга).
    /// Expense — контрагент должен нам (баланс +), Income — мы должны контрагенту (баланс −).
    /// </summary>
    public async Task<CashTransaction> AccrueAsync(CashDirection direction, int counterpartyId, decimal amount, string? article,
        string? comment, DateTime? date = null, CancellationToken ct = default)
    {
        Access.Demand(Perm.FinanceAccrualCreate);
        if (counterpartyId <= 0) throw new BusinessException("Выберите контрагента.");
        amount = Money.Round(amount);
        if (amount <= 0) throw new BusinessException("Сумма операции должна быть больше нуля.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var op = new CashTransaction
        {
            Date = date ?? DateTime.Now,
            CashRegisterId = null,
            Direction = direction,
            Kind = CashOperationKind.CounterpartyAccrual,
            Category = string.IsNullOrWhiteSpace(article) ? "Начисление контрагенту" : article.Trim(),
            Amount = amount,
            Comment = comment,
            DocumentNumber = await DocumentNumbers.NextAsync(db, DocumentNumbers.Cash, ct),
            UserId = session.UserId,
            CounterpartyId = counterpartyId
        };
        db.CashTransactions.Add(op);
        await db.SaveChangesAsync(ct);
        return op;
    }

    /// <summary>Наш текущий долг перед контрагентом по взаиморасчётам (0 — не должны или он должен нам).</summary>
    public async Task<decimal> GetSupplierDebtAsync(int supplierId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var balance = (await CounterpartyLedger.BalancesAsync(db, [supplierId], null, ct)).GetValueOrDefault(supplierId);
        return Math.Max(0, -balance);
    }

    public async Task<List<CashTransaction>> GetOperationsAsync(DateTime from, DateTime to, int? cashRegisterId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.CashTransactions.AsNoTracking().Include(t => t.CashRegister).Include(t => t.Counterparty)
            .Where(t => t.Date >= from.Date && t.Date < to.Date.AddDays(1));
        if (cashRegisterId is not null) q = q.Where(t => t.CashRegisterId == cashRegisterId);
        return await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToListAsync(ct);
    }

    /// <summary>Страница журнала операций: фильтр и постраничная выборка выполняются в базе.</summary>
    public async Task<CashOperationsPage> GetOperationsPageAsync(CashOperationFilter f, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.CashTransactions.AsNoTracking();
        if (!f.LatestAdded) q = q.Where(t => t.Date >= f.From.Date && t.Date < f.To.Date.AddDays(1));
        if (f.FinanceOnly)
            q = q.Where(t => t.Kind == CashOperationKind.Manual
                || t.Kind == CashOperationKind.TransferIn || t.Kind == CashOperationKind.TransferOut
                || t.Kind == CashOperationKind.CounterpartyPayment || t.Kind == CashOperationKind.CounterpartyAccrual
                // «Платёж контрагенту» с долгом по закупкам проводится как оплата поставщику, но без документа закупки
                || (t.Kind == CashOperationKind.SupplierPayment && t.DocumentId == null));
        if (f.CashRegisterId is { } cash) q = q.Where(t => t.CashRegisterId == cash);
        if (f.Direction is { } dir) q = q.Where(t => t.Direction == dir);
        if (f.Kinds is { Length: > 0 } kinds) q = q.Where(t => kinds.Contains(t.Kind));
        if (f.CounterpartyId is { } cp) q = q.Where(t => t.CounterpartyId == cp);

        // Видны только подразделы, на просмотр которых есть право; операции документов — по доступу к модулю
        bool inc = Access.Can(Perm.FinanceIncomeView), exp = Access.Can(Perm.FinanceExpenseView),
            pay = Access.Can(Perm.FinancePaymentView), acr = Access.Can(Perm.FinanceAccrualView), trf = Access.Can(Perm.FinanceTransferView);
        if (!(inc && exp && pay && acr && trf))
            q = q.Where(t =>
                (t.Kind == CashOperationKind.Manual && (t.Direction == CashDirection.Income ? inc : exp))
                || ((t.Kind == CashOperationKind.CounterpartyPayment || t.Kind == CashOperationKind.SupplierPayment) && pay)
                || (t.Kind == CashOperationKind.CounterpartyAccrual && acr)
                || ((t.Kind == CashOperationKind.TransferIn || t.Kind == CashOperationKind.TransferOut) && trf)
                || t.Kind == CashOperationKind.Sale || t.Kind == CashOperationKind.SaleReturn || t.Kind == CashOperationKind.PurchaseReturn);

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            // SQLite сравнивает кириллицу с учётом регистра — ищем несколько вариантов написания
            var term = f.Search.Trim();
            var lower = term.ToLowerInvariant();
            var title = char.ToUpperInvariant(lower[0]) + lower[1..];
            q = q.Where(t => t.Category.Contains(term) || t.Category.Contains(lower) || t.Category.Contains(title)
                || (t.Comment != null && (t.Comment.Contains(term) || t.Comment.Contains(lower) || t.Comment.Contains(title)))
                || (t.DocumentNumber != null && t.DocumentNumber.Contains(term.ToUpperInvariant())));
        }

        // Итоги: SQLite не суммирует decimal, поэтому считаем по лёгкой проекции
        var sums = await q.Select(t => new { t.Direction, t.Kind, t.Amount }).ToListAsync(ct);
        var income = sums.Where(o => o.Direction == CashDirection.Income && o.Kind is not (CashOperationKind.TransferIn or CashOperationKind.CounterpartyAccrual)).Sum(o => o.Amount);
        var expense = sums.Where(o => o.Direction == CashDirection.Expense && o.Kind is not (CashOperationKind.TransferOut or CashOperationKind.CounterpartyAccrual)).Sum(o => o.Amount);

        var withRefs = q.Include(t => t.CashRegister).Include(t => t.Counterparty);
        var ordered = f.LatestAdded
            ? withRefs.OrderByDescending(t => t.Id)
            : withRefs.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id);
        var items = await ordered
            .Skip(Math.Max(0, skip)).Take(Math.Max(1, take)).ToListAsync(ct);
        return new CashOperationsPage(items, sums.Count, Money.Round(income), Money.Round(expense));
    }

    /// <summary>Сверка: остаток кассы должен совпадать с суммой журнала операций.</summary>
    public async Task<decimal> CalculateLedgerBalanceAsync(int cashRegisterId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ops = await db.CashTransactions.AsNoTracking().Where(t => t.CashRegisterId == cashRegisterId).ToListAsync(ct);
        return Money.Round(ops.Sum(o => o.SignedAmount));
    }
}
