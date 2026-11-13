using System.Windows;
using Microsoft.Win32;
using MiniTrade.Desktop.Views;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Абстракция диалогов, чтобы ViewModel не зависели от окон.</summary>
public interface IDialogService
{
    void Info(string message, string title = "TIJORAT MINI");
    void Error(string message, string title = "Ошибка");
    bool Confirm(string message, string title = "Подтверждение");
    string? SaveFile(string defaultName, string filter);
    string? OpenFile(string filter, string? initialDirectory = null);
    string? PickFolder(string? initialDirectory = null);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    public void Info(string message, string title = "TIJORAT MINI") => Show(message, title, MessageKind.Info);

    public void Error(string message, string title = "Ошибка") => Show(message, title, MessageKind.Warning);

    public bool Confirm(string message, string title = "Подтверждение") => Show(message, title, MessageKind.Question);

    public string? SaveFile(string defaultName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = defaultName, Filter = filter };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var dlg = new OpenFileDialog { Filter = filter, InitialDirectory = initialDirectory ?? "" };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickFolder(string? initialDirectory = null)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = initialDirectory ?? "" };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    // Заголовки по умолчанию («Ошибка», «Подтверждение») — константы, поэтому переводятся здесь;
    // уже переведённый текст через L.T проходит без изменений
    private static bool Show(string message, string title, MessageKind kind) =>
        MessageWindow.Show(L.T(message), L.T(title), kind);
}
