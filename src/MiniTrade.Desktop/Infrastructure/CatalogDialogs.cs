using System.Collections.ObjectModel;
using System.Windows;
using MiniTrade.Core.Domain;
using MiniTrade.Core.Services;
using MiniTrade.Desktop.ViewModels;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Итог модального окна справочника: сохранённая запись или признак удаления.</summary>
public sealed record CatalogEditResult<T>(T? Saved, bool Deleted) where T : class;

/// <summary>
/// Добавление и изменение записей справочников в модальном окне — из любого места программы.
/// Возвращает <c>null</c>, если пользователь закрыл окно без изменений.
/// </summary>
public interface ICatalogDialogs
{
    /// <param name="existing">Запись для изменения. <c>null</c> или запись с Id = 0 — создание новой
    /// (поля такой записи используются как заготовка, например штрихкод).</param>
    Task<CatalogEditResult<T>?> EditAsync<T>(T? existing = null) where T : class, ICatalogEntity, new();

    /// <summary>То же, но дополнительно обновляет список выбора (ComboBox) на месте:
    /// возвращаемый <see cref="CatalogEditResult{T}.Saved"/> — экземпляр из этого списка.</summary>
    Task<CatalogEditResult<T>?> EditAsync<T>(ObservableCollection<T> lookup, T? existing = null, bool activeOnly = true)
        where T : class, ICatalogEntity, new();
}

public sealed class CatalogDialogs(CatalogService catalog, IDialogService dialogs) : ICatalogDialogs
{
    public async Task<CatalogEditResult<T>?> EditAsync<T>(T? existing = null) where T : class, ICatalogEntity, new()
    {
        var item = existing is null ? new T() : CatalogEntities.Clone(existing);
        var vm = new CatalogEditViewModel<T>(item, catalog, dialogs, this);
        await vm.InitAsync();

        ModalHost.ShowDialog(new CatalogEditWindow { DataContext = vm });
        return vm.Result;
    }

    public async Task<CatalogEditResult<T>?> EditAsync<T>(ObservableCollection<T> lookup, T? existing = null, bool activeOnly = true)
        where T : class, ICatalogEntity, new()
    {
        var result = await EditAsync(existing);
        if (result is null) return null;

        // Обновляем список на месте, чтобы не сбросить выбор в других полях, привязанных к нему же
        var id = result.Saved?.Id ?? existing?.Id ?? 0;
        var index = IndexOf(lookup, id);
        var saved = result.Saved;
        if (saved is null || (activeOnly && !saved.IsActive))
        {
            if (index >= 0) lookup.RemoveAt(index);
            return result with { Saved = null };
        }
        if (index >= 0) lookup[index] = saved;
        else
        {
            var at = 0;
            while (at < lookup.Count && string.Compare(lookup[at].Name, saved.Name, StringComparison.CurrentCultureIgnoreCase) < 0) at++;
            lookup.Insert(at, saved);
        }
        return result;
    }

    private static int IndexOf<T>(ObservableCollection<T> list, int id) where T : ICatalogEntity
    {
        if (id == 0) return -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }
}
