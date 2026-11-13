using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>
/// Оформление продажи в отдельном окне (покупатель, касса, скидка, оплата, комментарий)
/// и черновики — отложенные чеки, к которым можно вернуться.
/// </summary>
public partial class SalesViewModel
{
    // ── Оформление ──

    /// <summary>Продажа проведена — окно оформления закрывается.</summary>
    public event EventHandler? SalePosted;

    /// <summary>F9 / «Оформить»: открыть окно оформления (из окна F9 — провести).</summary>
    [RelayCommand]
    private void Checkout()
    {
        if (Lines.Count == 0) { Dialogs.Error(L.T("Добавьте товары в чек.")); return; }
        if (Lines.Any(l => l.Quantity <= 0)) { Dialogs.Error(L.T("Количество в чеке должно быть больше нуля.")); return; }
        LastResult = null;
        ModalHost.ShowDialog(new CheckoutWindow { DataContext = this });
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── Черновики ──

    public ObservableCollection<SaleDraft> Drafts { get; } = new();
    public int DraftsCount => Drafts.Count;
    public bool HasDrafts => Drafts.Count > 0;

    /// <summary>Чек открыт из черновика: «Отложить» обновит его, проведение — удалит.</summary>
    [ObservableProperty] private Guid? _currentDraftId;
    [ObservableProperty] private bool _isDraftsOpen;

    private void LoadDrafts()
    {
        Drafts.Clear();
        foreach (var d in SaleDraftStore.Load().OrderByDescending(d => d.Saved)) Drafts.Add(d);
        RaiseDrafts();
    }

    private void RaiseDrafts()
    {
        OnPropertyChanged(nameof(DraftsCount));
        OnPropertyChanged(nameof(HasDrafts));
    }

    private void PersistDrafts()
    {
        SaleDraftStore.Save(Drafts);
        RaiseDrafts();
    }

    /// <summary>Сохранить текущий чек черновиком (без очистки). Возвращает false, если чек пуст.</summary>
    private bool StoreCurrentAsDraft()
    {
        if (Lines.Count == 0) return false;
        var draft = new SaleDraft(
            CurrentDraftId ?? Guid.NewGuid(), DateTime.Now,
            Customer?.Name ?? Lines[0].Product.Name + (Lines.Count > 1 ? L.F(" и ещё {0}", Lines.Count - 1) : ""),
            ToPay, Lines.Count,
            Lines.Select(l => new SaleDraftLine(l.Product.Id, l.Quantity, l.Price)).ToList(),
            Warehouse?.Id, CashRegister?.Id, Customer?.Id, DiscountPercent, Comment);
        var old = Drafts.FirstOrDefault(d => d.Id == draft.Id);
        if (old is not null) Drafts.Remove(old);
        Drafts.Insert(0, draft);
        PersistDrafts();
        return true;
    }

    /// <summary>«Отложить» (F8): чек уходит в черновики, касса готова к следующему покупателю.</summary>
    [RelayCommand]
    private void SaveDraft()
    {
        if (!StoreCurrentAsDraft()) { Dialogs.Error(L.T("Чек пуст — нечего откладывать.")); return; }
        var total = ToPay;
        NewSale();
        LastResult = L.F("Чек отложен в черновики: {0}", Money.Format(total));
    }

    /// <summary>Открыть черновик в чеке. Текущий непустой чек сначала откладывается — ничего не теряется.</summary>
    [RelayCommand]
    private void OpenDraft(SaleDraft? draft)
    {
        if (draft is null) return;
        IsDraftsOpen = false;
        if (draft.Id != CurrentDraftId) StoreCurrentAsDraft();
        NewSale();

        var missing = 0;
        foreach (var line in draft.Lines)
        {
            var product = Products.FirstOrDefault(p => p.Id == line.ProductId);
            if (product is null) { missing++; continue; }
            AddOrMerge(product, line.Quantity, line.Price);
        }
        if (draft.WarehouseId is { } w && Warehouses.FirstOrDefault(x => x.Id == w) is { } wh) Warehouse = wh;
        if (draft.CashRegisterId is { } c && CashRegisters.FirstOrDefault(x => x.Id == c) is { } cr) CashRegister = cr;
        Customer = draft.CustomerId is { } cu ? Customers.FirstOrDefault(x => x.Id == cu) : null;
        DiscountPercent = draft.DiscountPercent;
        Comment = draft.Comment;
        CurrentDraftId = draft.Id;
        RaiseTotals();
        if (missing > 0)
            Dialogs.Error(L.F("Товаров из черновика больше нет в справочнике: {0}. Они не добавлены в чек.", missing));
    }

    [RelayCommand]
    private void DeleteDraft(SaleDraft? draft)
    {
        if (draft is null || !Dialogs.Confirm(L.F("Удалить черновик «{0}» на {1}?", draft.Title, Money.Format(draft.Total)))) return;
        Drafts.Remove(draft);
        if (CurrentDraftId == draft.Id) CurrentDraftId = null;
        PersistDrafts();
    }

    /// <summary>После проведения черновик, из которого был чек, больше не нужен.</summary>
    private void RemoveCurrentDraft()
    {
        if (CurrentDraftId is not { } id) return;
        if (Drafts.FirstOrDefault(d => d.Id == id) is { } d) Drafts.Remove(d);
        CurrentDraftId = null;
        PersistDrafts();
    }
}
