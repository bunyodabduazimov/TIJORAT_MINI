using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniTrade.Desktop.Infrastructure;

namespace MiniTrade.Desktop.Views;

/// <summary>Столбец таблицы витрины: флажок видимости. Меняет столбец сразу и сохраняет выбор в ui.json.</summary>
public partial class PosColumnOption(string key, string title, DataGridColumn column) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    public bool Visible
    {
        get => column.Visibility == Visibility.Visible;
        set
        {
            column.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            var hidden = App.Ui.PosHiddenColumns;
            hidden.Remove(Key);
            if (!value) hidden.Add(Key);
            App.Ui.Save();
            OnPropertyChanged();
        }
    }
}

/// <summary>Модальное окно «Параметры кассы» (модель — SalesViewModel, изменения применяются сразу).</summary>
public partial class PosSettingsWindow : Window, IModalWindow
{
    public PosSettingsWindow(IEnumerable<PosColumnOption> columns)
    {
        InitializeComponent();
        ColumnsList.ItemsSource = columns.ToList();
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
