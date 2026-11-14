using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Общая основа модальных окон кассовых операций (Views/OperationWindow): заголовок, касса, сумма,
/// комментарий, кнопка проведения и ошибка внутри окна. Форма выбирается по типу модели.
/// </summary>
public abstract partial class OperationViewModel : ObservableObject, ICloseRequest
{
    protected OperationViewModel(FinanceService finance, ICatalogDialogs catalogDialogs, IReadOnlyList<CashRegister> cashRegisters)
    {
        Finance = finance;
        CatalogDialogs = catalogDialogs;
        CashRegisters = new ObservableCollection<CashRegister>(cashRegisters);
        _cashRegister = CashRegisters.FirstOrDefault();
    }

    protected FinanceService Finance { get; }
    protected ICatalogDialogs CatalogDialogs { get; }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Заголовок и кнопка для новой операции; при изменении — «Операция КО-000003» и «Сохранить».</summary>
    protected abstract string NewTitle { get; }
    protected abstract string NewSubmitText { get; }
    public string Title => EditingId is null ? NewTitle : L.F("Операция {0}", EditingNumber ?? "");
    public string SubmitText => EditingId is null ? NewSubmitText : L.T("Сохранить");

    /// <summary>Id изменяемой операции; null — новая операция.</summary>
    public int? EditingId { get; private set; }
    private string? EditingNumber { get; set; }

    /// <summary>Заполнить форму по операции журнала: для «Изменить» (asEdit) или «Скопировать».</summary>
    public void FillFrom(CashTransaction t, bool asEdit)
    {
        if (asEdit) { EditingId = t.Id; EditingNumber = t.DocumentNumber; _originalDate = t.Date; }
        OperationDate = asEdit ? t.Date.Date : DateTime.Today;
        CashRegister = CashRegisters.FirstOrDefault(c => c.Id == t.CashRegisterId) ?? CashRegister;
        Amount = t.Amount;
        Comment = t.Comment;
        FillDetails(t);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SubmitText));
    }

    protected virtual void FillDetails(CashTransaction t) { }
    /// <summary>Значок в заголовке (Material Design Icons) и его цвет — подсказка, что за операция.</summary>
    public abstract string Icon { get; }
    public virtual bool IsExpenseLike => false;
    public virtual bool IsIncomeLike => false;
    public virtual double DialogWidth => 460;
    /// <summary>Нужна ли касса (у начисления контрагенту её нет).</summary>
    protected virtual bool RequiresCashRegister => true;

    /// <summary>Операция проведена — журнал нужно обновить.</summary>
    public bool Posted { get; private set; }

    public ObservableCollection<CashRegister> CashRegisters { get; }
    [ObservableProperty] private CashRegister? _cashRegister;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string? _comment;

    /// <summary>Дата проводки: операцию можно провести задним числом.</summary>
    [ObservableProperty] private DateTime? _operationDate = DateTime.Today;
    private DateTime? _originalDate;

    /// <summary>Показывать ли поле даты (у оплаты закупки дата — текущая).</summary>
    public virtual bool ShowDate => true;

    /// <summary>Выбранный день + текущее время; при правке без смены дня исходное время сохраняется.</summary>
    protected DateTime PostingDate
    {
        get
        {
            var day = (OperationDate ?? DateTime.Today).Date;
            if (_originalDate is { } o && o.Date == day) return o;
            return day == DateTime.Today ? DateTime.Now : day + DateTime.Now.TimeOfDay;
        }
    }
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    public virtual Task InitAsync() => Task.CompletedTask;

    /// <summary>Изменить выбранную статью справочника; вернуть новое имя (null — отмена, "" — статья ушла в другое направление).</summary>
    protected async Task<string?> EditArticleInAsync(ObservableCollection<FinanceArticle> articles, string? name, CashDirection direction)
    {
        if (articles.FirstOrDefault(a => a.Name == name) is not { } current) return null;
        var r = await CatalogDialogs.EditAsync(articles, current);
        if (r?.Saved is not { } a) return null;
        if (a.Direction != direction) { articles.Remove(a); return ""; }
        return a.Name;
    }

    protected abstract Task PostAsync();

    private bool CanSubmit => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (RequiresCashRegister && CashRegister is null) throw new BusinessException("Выберите кассу.");
            if (ShowDate && OperationDate is null) throw new BusinessException("Укажите дату проводки.");
            if (ShowDate && OperationDate!.Value.Date > DateTime.Today) throw new BusinessException("Дата проводки не может быть в будущем.");
            await PostAsync();
            Posted = true;
            CloseRequested?.Invoke(this, true);
        }
        catch (BusinessException ex) { ErrorMessage = L.Message(ex); }
        catch (Exception ex) { ErrorLog.Write(ex, GetType().Name); ErrorMessage = L.F("Непредвиденная ошибка: {0}", ex.Message); }
        finally { IsBusy = false; }
    }
}

/// <summary>Приход или расход по статье.</summary>
public partial class CashOperationViewModel(
    CashDirection direction, FinanceService finance, ICatalogDialogs catalogDialogs, IReadOnlyList<CashRegister> cashRegisters)
    : OperationViewModel(finance, catalogDialogs, cashRegisters)
{
    public CashDirection Direction { get; } = direction;
    protected override string NewTitle => Direction == CashDirection.Income ? L.T("Приход денег") : L.T("Расход денег");
    protected override string NewSubmitText => Direction == CashDirection.Income ? L.T("Провести приход") : L.T("Провести расход");

    protected override void FillDetails(CashTransaction t) => Article = t.Category;
    public override string Icon => Direction == CashDirection.Income ? "\U000F1261" : "\U000F1260";
    public override bool IsExpenseLike => Direction == CashDirection.Expense;
    public override bool IsIncomeLike => Direction == CashDirection.Income;

    public ObservableCollection<FinanceArticle> Articles { get; } = new();
    [ObservableProperty] private string _article = "";

    public override async Task InitAsync()
    {
        foreach (var a in await Finance.GetArticlesAsync(Direction)) Articles.Add(a);
        Article = Articles.FirstOrDefault()?.Name ?? "";
    }

    [RelayCommand]
    private async Task AddArticleAsync()
    {
        var r = await CatalogDialogs.EditAsync(Articles, new FinanceArticle { Direction = Direction });
        if (r?.Saved is not { } a) return;
        if (a.Direction != Direction) { Articles.Remove(a); return; }
        Article = a.Name;
    }

    [RelayCommand]
    private async Task EditArticleAsync()
    {
        if (await EditArticleInAsync(Articles, Article, Direction) is { } name) Article = name;
    }

    protected override Task PostAsync() => EditingId is { } id
        ? Finance.UpdateOperationAsync(id, CashRegister!.Id, Article, Amount, Comment, date: PostingDate)
        : Finance.AddOperationAsync(CashRegister!.Id, Direction, Article, Amount, Comment, PostingDate);
}

/// <summary>Перемещение денег между кассами.</summary>
public partial class TransferViewModel : OperationViewModel
{
    public TransferViewModel(FinanceService finance, ICatalogDialogs catalogDialogs, IReadOnlyList<CashRegister> cashRegisters)
        : base(finance, catalogDialogs, cashRegisters)
    {
        _targetCash = CashRegisters.Skip(1).FirstOrDefault();
    }

    protected override string NewTitle => L.T("Перемещение между кассами");
    protected override string NewSubmitText => L.T("Переместить");

    /// <summary>Копия перемещения: строка «приход» — это касса-получатель.</summary>
    protected override void FillDetails(CashTransaction t)
    {
        if (t.Kind != CashOperationKind.TransferIn) return;
        TargetCash = CashRegister;
        CashRegister = CashRegisters.FirstOrDefault(c => c.Id != TargetCash?.Id) ?? CashRegister;
    }
    public override string Icon => "\U000F04E1";

    [ObservableProperty] private CashRegister? _targetCash;

    protected override Task PostAsync()
    {
        if (TargetCash is null) throw new BusinessException("Выберите кассы.");
        return Finance.TransferAsync(CashRegister!.Id, TargetCash.Id, Amount, string.IsNullOrWhiteSpace(Comment) ? L.T("Перемещение") : Comment, PostingDate);
    }
}

/// <summary>Оплата долга по конкретной закупке (из раздела «Закуп»).</summary>
public partial class PurchasePaymentViewModel : OperationViewModel
{
    private readonly PurchaseService _purchases;

    public PurchasePaymentViewModel(Purchase purchase, PurchaseService purchases, FinanceService finance,
        ICatalogDialogs catalogDialogs, IReadOnlyList<CashRegister> cashRegisters)
        : base(finance, catalogDialogs, cashRegisters)
    {
        _purchases = purchases;
        Purchase = purchase;
        Amount = purchase.Debt;
    }

    public Purchase Purchase { get; }
    protected override string NewTitle => L.F("Оплата закупки {0}", Purchase.Number ?? "");
    protected override string NewSubmitText => L.T("Оплатить");
    public override string Icon => "\U000F019B";
    public override bool IsExpenseLike => true;
    public override bool ShowDate => false;

    protected override Task PostAsync()
    {
        if (Amount <= 0) throw new BusinessException("Сумма оплаты должна быть больше нуля.");
        if (Amount > Purchase.Debt) throw new BusinessException(L.F("Сумма превышает задолженность ({0}).", Money.Format(Purchase.Debt)));
        return _purchases.PayAsync(Purchase.Id, Amount, CashRegister!.Id, Comment);
    }
}

/// <summary>Платёж контрагенту (расход) или от контрагента (приход).</summary>
public partial class CounterpartyPaymentViewModel(
    CashDirection direction, FinanceService finance, CatalogService catalog, ICatalogDialogs catalogDialogs,
    IReadOnlyList<CashRegister> cashRegisters)
    : OperationViewModel(finance, catalogDialogs, cashRegisters)
{
    public CashDirection Direction { get; } = direction;
    protected override string NewTitle => Direction == CashDirection.Expense ? L.T("Платёж контрагенту") : L.T("Платёж от контрагента");
    protected override string NewSubmitText => Direction == CashDirection.Expense ? L.T("Оплатить") : L.T("Принять оплату");

    protected override void FillDetails(CashTransaction t)
    {
        EnsureArticle(t.Category);
        Article = t.Category;
        Counterparty = Counterparties.FirstOrDefault(c => c.Id == t.CounterpartyId);
    }

    public ObservableCollection<FinanceArticle> Articles { get; } = new();

    /// <summary>Статья документа, которой нет в справочнике (старые записи, статья по умолчанию), — добавить в список, чтобы её можно было выбрать.</summary>
    private void EnsureArticle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Articles.Any(a => a.Name == name)) return;
        Articles.Insert(0, new FinanceArticle { Name = name, Direction = Direction });
    }

    [RelayCommand]
    private async Task AddArticleAsync()
    {
        var r = await CatalogDialogs.EditAsync(Articles, new FinanceArticle { Direction = Direction });
        if (r?.Saved is not { } a) return;
        if (a.Direction != Direction) { Articles.Remove(a); return; }
        Article = a.Name;
    }

    [RelayCommand]
    private async Task EditArticleAsync()
    {
        if (await EditArticleInAsync(Articles, Article, Direction) is { } name) Article = name;
    }
    public override string Icon => Direction == CashDirection.Expense ? "\U000F0054" : "\U000F004D";
    public override bool IsExpenseLike => Direction == CashDirection.Expense;
    public override bool IsIncomeLike => Direction == CashDirection.Income;
    public override double DialogWidth => 500;

    public ObservableCollection<Counterparty> Counterparties { get; } = new();

    [ObservableProperty] private Counterparty? _counterparty;
    [ObservableProperty] private string? _article;

    /// <summary>Долг перед выбранным контрагентом по закупкам — подсказка при оплате.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebtText))]
    private decimal? _supplierDebt;

    public string? DebtText => EditingId is null && Direction == CashDirection.Expense && SupplierDebt is { } d && d > 0
        ? L.F("Долг по закупкам: {0}. Оплата погасит закупки начиная с самых старых.", Money.Format(d))
        : null;

    public override async Task InitAsync()
    {
        foreach (var c in await catalog.GetAllAsync<Counterparty>(true)) Counterparties.Add(c);
        foreach (var a in await Finance.GetArticlesAsync(Direction)) Articles.Add(a);
        var defaultArticle = Direction == CashDirection.Expense ? "Оплата контрагенту" : "Оплата от контрагента";
        EnsureArticle(defaultArticle);
        Article = defaultArticle;
    }

    partial void OnCounterpartyChanged(Counterparty? value) => _ = LoadDebtAsync(value);

    private async Task LoadDebtAsync(Counterparty? c)
    {
        SupplierDebt = null;
        if (c is null || Direction != CashDirection.Expense) return;
        var debt = await Finance.GetSupplierDebtAsync(c.Id);
        if (Counterparty?.Id == c.Id) SupplierDebt = debt;
    }

    [RelayCommand]
    private async Task AddCounterpartyAsync()
    {
        if (await CatalogDialogs.EditAsync(Counterparties) is { Saved: { } c }) Counterparty = c;
    }

    [RelayCommand]
    private async Task EditCounterpartyAsync()
    {
        if (Counterparty is null) return;
        if (await CatalogDialogs.EditAsync(Counterparties, Counterparty) is { } r) Counterparty = r.Saved;
    }

    protected override Task PostAsync()
    {
        if (Counterparty is null) throw new BusinessException("Выберите контрагента.");
        if (EditingId is { } id)
            return Finance.UpdateOperationAsync(id, CashRegister!.Id, string.IsNullOrWhiteSpace(Article) ? NewTitle : Article, Amount, Comment, Counterparty.Id, PostingDate);
        return Finance.CounterpartyPaymentAsync(new CounterpartyPaymentRequest(Direction, CashRegister!.Id,
            Counterparty.Id, Amount, Article, Comment, PostingDate));
    }
}

/// <summary>
/// Начисление контрагенту без кассы: меняет только взаиморасчёты (услуги, бонусы, корректировка долга).
/// «Контрагент должен нам» — Expense (баланс +), «Мы должны контрагенту» — Income (баланс −).
/// </summary>
public partial class CounterpartyAccrualViewModel(
    FinanceService finance, CatalogService catalog, ICatalogDialogs catalogDialogs, IReadOnlyList<CashRegister> cashRegisters)
    : OperationViewModel(finance, catalogDialogs, cashRegisters)
{
    protected override string NewTitle => L.T("Начисление контрагенту");
    protected override string NewSubmitText => L.T("Начислить");
    protected override bool RequiresCashRegister => false;
    public override string Icon => "\U000F0004";
    public bool IsNew => EditingId is null;
    public override double DialogWidth => 500;

    public ObservableCollection<Counterparty> Counterparties { get; } = new();
    [ObservableProperty] private Counterparty? _counterparty;
    [ObservableProperty] private string? _article = L.T("Начисление контрагенту");

    /// <summary>true — контрагент должен нам; false — мы должны контрагенту.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TheyOweUs))]
    private bool _weOwe;

    public bool TheyOweUs { get => !WeOwe; set => WeOwe = !value; }

    /// <summary>Статьи справочника: «мы должны» — расходные (услуги, работа), «должен нам» — приходные (штраф).</summary>
    public ObservableCollection<FinanceArticle> Articles { get; } = new();
    private CashDirection ArticleDirection => WeOwe ? CashDirection.Expense : CashDirection.Income;
    private string DefaultArticle => L.T("Начисление контрагенту");

    protected override void FillDetails(CashTransaction t)
    {
        WeOwe = t.Direction == CashDirection.Income;
        EnsureArticle(t.Category);
        Article = t.Category;
        Counterparty = Counterparties.FirstOrDefault(c => c.Id == t.CounterpartyId);
    }

    public override async Task InitAsync()
    {
        foreach (var c in await catalog.GetAllAsync<Counterparty>(true)) Counterparties.Add(c);
        await LoadArticlesAsync();
    }

    partial void OnWeOweChanged(bool value) => _ = LoadArticlesAsync();

    private async Task LoadArticlesAsync()
    {
        var list = await Finance.GetArticlesAsync(ArticleDirection);
        var keep = Article;
        Articles.Clear();
        foreach (var a in list) Articles.Add(a);
        EnsureArticle(DefaultArticle);
        // При правке сохраняем статью документа; при смене вида — статья другого направления сбрасывается
        if (!IsNew) EnsureArticle(keep);
        Article = !string.IsNullOrWhiteSpace(keep) && Articles.Any(a => a.Name == keep) ? keep : DefaultArticle;
    }

    /// <summary>Статья, которой нет в справочнике (по умолчанию, старые записи), — добавить в список.</summary>
    private void EnsureArticle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Articles.Any(a => a.Name == name)) return;
        Articles.Insert(0, new FinanceArticle { Name = name, Direction = ArticleDirection });
    }

    [RelayCommand]
    private async Task AddArticleAsync()
    {
        var dir = ArticleDirection;
        var r = await CatalogDialogs.EditAsync(Articles, new FinanceArticle { Direction = dir });
        if (r?.Saved is not { } a) return;
        if (a.Direction != dir) { Articles.Remove(a); return; }
        Article = a.Name;
    }

    [RelayCommand]
    private async Task EditArticleAsync()
    {
        if (await EditArticleInAsync(Articles, Article, ArticleDirection) is { } name) Article = name;
    }

    [RelayCommand]
    private async Task EditCounterpartyAsync()
    {
        if (Counterparty is null) return;
        if (await CatalogDialogs.EditAsync(Counterparties, Counterparty) is { } r) Counterparty = r.Saved;
    }

    [RelayCommand]
    private async Task AddCounterpartyAsync()
    {
        if (await CatalogDialogs.EditAsync(Counterparties) is { Saved: { } c }) Counterparty = c;
    }

    protected override Task PostAsync()
    {
        if (Counterparty is null) throw new BusinessException("Выберите контрагента.");
        var article = string.IsNullOrWhiteSpace(Article) ? NewTitle : Article;
        if (EditingId is { } id)
            return Finance.UpdateOperationAsync(id, null, article, Amount, Comment, Counterparty.Id, PostingDate);
        return Finance.AccrueAsync(WeOwe ? CashDirection.Income : CashDirection.Expense, Counterparty.Id, Amount, article, Comment, PostingDate);
    }
}
