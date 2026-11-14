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
            Id = p.Id, Name = p.Name, IsService = p.IsService, IsWeighted = p.IsWeighted, Sku = p.Sku, Barcode = p.Barcode, Unit = p.Unit, CategoryId = p.CategoryId,
            PurchasePrice = p.PurchasePrice, SalePrice = p.SalePrice, MinStock = p.MinStock, IsActive = p.IsActive
        },
        MeasureUnit u => (T)(object)new MeasureUnit { Id = u.Id, Name = u.Name, Description = u.Description, IsActive = u.IsActive },
        ScaleDevice s => (T)(object)new ScaleDevice { Id = s.Id, Name = s.Name, Type = s.Type, Ip = s.Ip, Port = s.Port, FilePath = s.FilePath, IsActive = s.IsActive },
        Category c => (T)(object)new Category { Id = c.Id, Name = c.Name, Description = c.Description, Image = c.Image, IsActive = c.IsActive },
        Counterparty c => (T)(object)new Counterparty { Id = c.Id, Name = c.Name, GroupId = c.GroupId, Phone = c.Phone, Address = c.Address, Note = c.Note, IsActive = c.IsActive },
        CounterpartyGroup g => (T)(object)new CounterpartyGroup { Id = g.Id, Name = g.Name, Description = g.Description, IsActive = g.IsActive },
        Warehouse w => (T)(object)new Warehouse { Id = w.Id, Name = w.Name, Address = w.Address, IsActive = w.IsActive },
        CashRegister c => (T)(object)new CashRegister { Id = c.Id, Name = c.Name, Balance = c.Balance, IsCashless = c.IsCashless, IsActive = c.IsActive },
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
            Product { IsService: true } => (L.T("Услуга"), L.T("Новый товар / услуга")),
            Product => (L.T("Товар"), L.T("Новый товар / услуга")),
            Category => (L.T("Категория"), L.T("Новая категория")),
            MeasureUnit => (L.T("Единица измерения"), L.T("Новая единица измерения")),
            ScaleDevice => (L.T("Весы"), L.T("Новые весы")),
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
    public ObservableCollection<MeasureUnit> Units { get; } = new();
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCategoryCommand))]
    private Category? _selectedCategory;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditUnitCommand))]
    private MeasureUnit? _selectedUnit;

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
            // Новый контрагент: телефон начинается с кода страны компании
            if (cp.Id == 0 && string.IsNullOrWhiteSpace(cp.Phone) && Countries.PhoneCode.Length > 0) cp.Phone = Countries.PhoneCode + " ";
            return;
        }
        if (Item is Category cat) { PhotoImage = PhotoProcessor.ToImage(_photo = cat.Image); return; }
        if (Item is not Product product) return;
        var all = await _catalog.GetAllAsync<Category>();
        foreach (var c in all.Where(c => c.IsActive || c.Id == product.CategoryId)) Categories.Add(c);
        SelectedCategory = Categories.FirstOrDefault(c => c.Id == product.CategoryId);
        foreach (var u in await _catalog.GetAllAsync<MeasureUnit>(activeOnly: true)) Units.Add(u);
        var unitName = string.IsNullOrWhiteSpace(product.Unit) ? "шт" : product.Unit;
        SelectedUnit = Units.FirstOrDefault(u => string.Equals(u.Name, unitName, StringComparison.CurrentCultureIgnoreCase));
        if (SelectedUnit is null && product.Id != 0)
        {
            // Единица товара убрана из справочника — показываем её, чтобы не потерять при сохранении
            var legacy = new MeasureUnit { Name = unitName };
            Units.Add(legacy);
            SelectedUnit = legacy;
        }
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
            if (Item is Product p)
            {
                p.CategoryId = SelectedCategory?.Id;
                p.Unit = SelectedUnit?.Name ?? "шт";
            }
            if (Item is Counterparty c)
            {
                c.GroupId = SelectedGroup?.Id;
                if (c.Phone?.Trim() == Countries.PhoneCode) c.Phone = null; // остался только код страны
            }
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

    // Только для весов: результат проверки связи
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty] private bool? _testOk;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTesting;

    private bool CanTest => !IsTesting;

    /// <summary>Проверка связи с весами по введённым IP и порту (без сохранения).</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (Item is not ScaleDevice s) return;
        var ip = s.Ip?.Trim();
        if (string.IsNullOrEmpty(ip) || s.Port is <= 0 or > 65535)
        {
            TestOk = false;
            TestMessage = L.T("Укажите IP-адрес и порт");
            return;
        }
        IsTesting = true;
        TestOk = null;
        TestMessage = L.T("Проверка связи…");
        try
        {
            var ok = await ScaleSyncService.TestConnectionAsync(ip, s.Port);
            TestOk = ok;
            TestMessage = ok
                ? L.F("Весы доступны ({0}:{1})", ip, s.Port)
                : L.F("Нет связи с {0}:{1}", ip, s.Port);
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestMessage = ex.Message;
        }
        finally { IsTesting = false; }
    }

    /// <summary>Новая группа прямо из карточки контрагента.</summary>
    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var r = await _catalogDialogs.EditAsync(Groups);
        if (r?.Saved is { } g) SelectedGroup = g;
    }

    /// <summary>Изменить выбранную группу прямо из карточки контрагента.</summary>
    [RelayCommand]
    private async Task EditGroupAsync()
    {
        if (SelectedGroup is not { } current) return;
        var r = await _catalogDialogs.EditAsync(Groups, current);
        if (r is not null) SelectedGroup = r.Saved;
    }

    /// <summary>Свободный штрихкод для товара (весовой — код весов, иначе EAN-13). null — ошибка показана в окне.</summary>
    public async Task<string?> GenerateBarcodeAsync(bool replacing)
    {
        if (Item is not Product p) return null;
        if (replacing && !_dialogs.Confirm(L.T("Заменить текущий штрихкод новым?"))) return null;
        ErrorMessage = null;
        try { return await _catalog.GenerateBarcodeAsync(p.IsWeighted, p.Id); }
        catch (BusinessException ex) { ErrorMessage = L.Message(ex); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        return null;
    }

    /// <summary>Новая категория прямо из карточки товара.</summary>
    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var r = await _catalogDialogs.EditAsync(Categories);
        if (r?.Saved is { } c) SelectedCategory = c;
    }

    private bool CanEditCategory => SelectedCategory is { Id: > 0 };

    /// <summary>Изменить выбранную категорию прямо из карточки товара.</summary>
    [RelayCommand(CanExecute = nameof(CanEditCategory))]
    private async Task EditCategoryAsync()
    {
        if (SelectedCategory is not { } current) return;
        var r = await _catalogDialogs.EditAsync(Categories, current);
        if (r is not null) SelectedCategory = r.Saved;
    }

    /// <summary>Новая единица измерения прямо из карточки товара.</summary>
    [RelayCommand]
    private async Task AddUnitAsync()
    {
        var r = await _catalogDialogs.EditAsync(Units);
        if (r?.Saved is { } u) SelectedUnit = u;
    }

    private bool CanEditUnit => SelectedUnit is { Id: > 0 };

    /// <summary>Изменить выбранную единицу (переименование обновит и товары с ней).</summary>
    [RelayCommand(CanExecute = nameof(CanEditUnit))]
    private async Task EditUnitAsync()
    {
        if (SelectedUnit is not { } current) return;
        var r = await _catalogDialogs.EditAsync(Units, current);
        if (r is not null) SelectedUnit = r.Saved;
    }
}
