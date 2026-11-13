using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniTrade.Core.Common;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Копирование записей и подписи для справочников.</summary>
public static class CatalogEntities
{
    /// <summary>Копия только скалярных полей — форма правит копию, а не запись из списка.</summary>
    public static T Clone<T>(T e) where T : class, ICatalogEntity => e switch
    {
        Product p => (T)(object)new Product
        {
            Id = p.Id, Name = p.Name, Sku = p.Sku, Barcode = p.Barcode, Unit = p.Unit, CategoryId = p.CategoryId,
            PurchasePrice = p.PurchasePrice, SalePrice = p.SalePrice, MinStock = p.MinStock, IsActive = p.IsActive
        },
        Category c => (T)(object)new Category { Id = c.Id, Name = c.Name, Description = c.Description, Image = c.Image, IsActive = c.IsActive },
        Counterparty c => (T)(object)new Counterparty { Id = c.Id, Name = c.Name, GroupId = c.GroupId, Phone = c.Phone, Address = c.Address, Note = c.Note, IsActive = c.IsActive },
        CounterpartyGroup g => (T)(object)new CounterpartyGroup { Id = g.Id, Name = g.Name, Description = g.Description, IsActive = g.IsActive },
        Warehouse w => (T)(object)new Warehouse { Id = w.Id, Name = w.Name, Address = w.Address, IsActive = w.IsActive },
        CashRegister c => (T)(object)new CashRegister { Id = c.Id, Name = c.Name, Balance = c.Balance, IsActive = c.IsActive },
        FinanceArticle a => (T)(object)new FinanceArticle { Id = a.Id, Name = a.Name, Direction = a.Direction, Description = a.Description, IsActive = a.IsActive },
        _ => throw new NotSupportedException(typeof(T).Name)
    };

    /// <summary>Заготовка новой записи по образцу: без Id, с пометкой «(копия)»;
    /// штрихкод не копируется (он должен быть уникальным), остаток кассы — тоже.</summary>
    public static T CopyOf<T>(T e) where T : class, ICatalogEntity
    {
        var c = Clone(e);
        c.Id = 0;
        c.IsActive = true;
        c.Name = L.F("{0} (копия)", e.Name);
        if (c is Product p) p.Barcode = null;
        if (c is CashRegister cr) cr.Balance = 0;
        return c;
    }

    /// <summary>Заголовок модального окна: «Новый товар», «Категория: Напитки».</summary>
    public static string DialogTitle(ICatalogEntity e)
    {
        var (singular, isNewTitle) = e switch
        {
            Product => (L.T("Товар"), L.T("Новый товар")),
            Category => (L.T("Категория"), L.T("Новая категория")),
            Counterparty => (L.T("Контрагент"), L.T("Новый контрагент")),
            CounterpartyGroup => (L.T("Группа контрагентов"), L.T("Новая группа контрагентов")),
            Warehouse => (L.T("Склад"), L.T("Новый склад")),
            CashRegister => (L.T("Касса"), L.T("Новая касса")),
            FinanceArticle { Direction: CashDirection.Income } => (L.T("Статья дохода"), L.T("Новая статья дохода")),
            FinanceArticle => (L.T("Статья расхода"), L.T("Новая статья расхода")),
            _ => (L.T("Запись"), L.T("Новая запись"))
        };
        return e.Id == 0 ? isNewTitle : $"{singular}: {e.Name}";
    }
}

/// <summary>Что окну нужно от модели: ширина формы и сигнал закрыться (true — запись сохранена/удалена).</summary>
public interface ICloseRequest
{
    double DialogWidth { get; }
    event EventHandler<bool>? CloseRequested;
}

/// <summary>Модель модального окна добавления/изменения записи справочника.</summary>
public partial class CatalogEditViewModel<T> : ObservableObject, ICloseRequest where T : class, ICatalogEntity, new()
{
    private readonly CatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly ICatalogDialogs _catalogDialogs;

    public CatalogEditViewModel(T item, CatalogService catalog, IDialogService dialogs, ICatalogDialogs catalogDialogs)
    {
        Item = item;
        _catalog = catalog;
        _dialogs = dialogs;
        _catalogDialogs = catalogDialogs;
        Title = CatalogEntities.DialogTitle(item);
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Редактируемая копия записи (форма выбирается по её типу).</summary>
    public T Item { get; }
    public string Title { get; }
    public bool IsNew => Item.Id == 0;
    /// <summary>Ширина окна по типу: товар — широкая форма в 2 колонки, остальное — мини-окна.</summary>
    public double DialogWidth => Item switch
    {
        Product => 860,
        Counterparty => 460,
        _ => 400
    };

    /// <summary>Итог для вызывающего кода; null — окно закрыто без изменений.</summary>
    public CatalogEditResult<T>? Result { get; private set; }

    // Только для товара
    public ObservableCollection<Category> Categories { get; } = new();
    public IReadOnlyList<string> Units { get; } = ["шт", "кг", "г", "л", "м", "уп", "кор"];
    [ObservableProperty] private Category? _selectedCategory;

    // Только для контрагента
    public ObservableCollection<CounterpartyGroup> Groups { get; } = new();
    [ObservableProperty] private CounterpartyGroup? _selectedGroup;

    // Фото товара: меняется в окне, в базу пишется только при «Сохранить»
    private byte[]? _photo;
    private byte[]? _photoThumbnail;
    private bool _photoChanged;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    [NotifyCanExecuteChangedFor(nameof(RemovePhotoCommand))]
    private System.Windows.Media.ImageSource? _photoImage;
    public bool HasPhoto => PhotoImage is not null;

    [RelayCommand]
    private void ChoosePhoto()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = PhotoProcessor.OpenFileFilter, Title = L.T("Фото товара") };
        if (dialog.ShowDialog() == true) SetPhotoFromFile(dialog.FileName);
    }

    /// <summary>Фото из файла (кнопка выбора или перетаскивание в окно).</summary>
    public void SetPhotoFromFile(string path)
    {
        try
        {
            (_photo, _photoThumbnail) = PhotoProcessor.FromFile(path);
            _photoChanged = true;
            PhotoImage = PhotoProcessor.ToImage(_photo);
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorMessage = L.T("Не удалось открыть изображение. Выберите файл JPG, PNG, BMP или GIF.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPhoto))]
    private void RemovePhoto()
    {
        _photo = _photoThumbnail = null;
        _photoChanged = true;
        PhotoImage = null;
    }

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    private bool _isBusy;

    public async Task InitAsync()
    {
        if (Item is Counterparty cp)
        {
            foreach (var g in (await _catalog.GetAllAsync<CounterpartyGroup>()).Where(g => g.IsActive || g.Id == cp.GroupId)) Groups.Add(g);
            SelectedGroup = Groups.FirstOrDefault(g => g.Id == cp.GroupId);
            return;
        }
        if (Item is Category cat) { PhotoImage = PhotoProcessor.ToImage(_photo = cat.Image); return; }
        if (Item is not Product product) return;
        var all = await _catalog.GetAllAsync<Category>();
        foreach (var c in all.Where(c => c.IsActive || c.Id == product.CategoryId)) Categories.Add(c);
        SelectedCategory = Categories.FirstOrDefault(c => c.Id == product.CategoryId);
        if (product.Id != 0) PhotoImage = PhotoProcessor.ToImage(_photo = await _catalog.GetPhotoAsync(product.Id));
    }

    private bool CanRun => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (Item is Product p) p.CategoryId = SelectedCategory?.Id;
            if (Item is Counterparty c) c.GroupId = SelectedGroup?.Id;
            if (Item is Category cat && _photoChanged) cat.Image = _photoThumbnail;
            var saved = await _catalog.SaveAsync(Item);
            if (saved is Counterparty sc) sc.Group = SelectedGroup;
            if (saved is Product sp)
            {
                sp.Category = SelectedCategory;
                if (_photoChanged) await _catalog.SetPhotoAsync(sp.Id, _photo, _photoThumbnail);
                sp.Thumbnail = _photoChanged ? _photoThumbnail : null;
            }
            Result = new(saved, false);
            CloseRequested?.Invoke(this, true);
        }
        catch (BusinessException ex) { ErrorMessage = L.Message(ex); }
        catch (Exception ex) { ErrorMessage = L.F("Не удалось сохранить: {0}", ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeleteAsync()
    {
        if (IsNew || !_dialogs.Confirm(L.F("Удалить «{0}»?", Item.Name))) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _catalog.DeleteAsync<T>(Item.Id);
            Result = new(null, true);
            CloseRequested?.Invoke(this, true);
        }
        catch (BusinessException ex) { ErrorMessage = L.Message(ex); }
        catch (Exception ex) { ErrorMessage = L.F("Не удалось удалить: {0}", ex.Message); }
        finally { IsBusy = false; }
    }

    /// <summary>Новая группа прямо из карточки контрагента.</summary>
    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var r = await _catalogDialogs.EditAsync(Groups);
        if (r?.Saved is { } g) SelectedGroup = g;
    }

    /// <summary>Новая категория прямо из карточки товара.</summary>
    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var r = await _catalogDialogs.EditAsync(Categories);
        if (r?.Saved is { } c) SelectedCategory = c;
    }
}
