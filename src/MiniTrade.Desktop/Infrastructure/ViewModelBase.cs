using CommunityToolkit.Mvvm.ComponentModel;
using MiniTrade.Core.Common;

namespace MiniTrade.Desktop.Infrastructure;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    protected IDialogService Dialogs { get; }

    protected ViewModelBase(IDialogService dialogs) => Dialogs = dialogs;

    /// <summary>Выполняет действие, показывая бизнес-ошибки пользователю.</summary>
    protected async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (BusinessException ex)
        {
            Dialogs.Error(L.Message(ex));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, GetType().Name);
            Dialogs.Error(L.F("Непредвиденная ошибка: {0}", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
        return false;
    }
}

/// <summary>Страница главного окна; загружает данные при открытии.</summary>
public abstract class PageViewModel(IDialogService dialogs) : ViewModelBase(dialogs)
{
    public abstract string Title { get; }
    public abstract Task LoadAsync();

    /// <summary>На странице есть несохранённый документ — при уходе в другой раздел нужно предупредить.</summary>
    public virtual bool HasUnsavedChanges => false;

    protected static void Fill<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
