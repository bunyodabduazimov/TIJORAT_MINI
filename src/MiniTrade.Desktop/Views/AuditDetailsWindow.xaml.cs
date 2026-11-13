using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

/// <summary>Подробности записи журнала: кто, когда, что; изменённые поля «было → стало» таблицей.</summary>
public partial class AuditDetailsWindow : Window, IModalWindow
{
    private readonly AuditRow _row;

    public AuditDetailsWindow(AuditRow row)
    {
        InitializeComponent();
        _row = row;
        var log = row.Log;
        Title = L.T("Подробности");
        ActionText.Text = row.ActionName;
        ActionText.SetResourceReference(TextBlock.ForegroundProperty,
            row.IsDelete ? "DangerBrush" : row.IsCreate ? "SuccessBrush" : "PrimaryBrush");
        TitleText.Text = string.IsNullOrWhiteSpace(log.EntityName) ? row.ObjectType : $"{row.ObjectType}: {log.EntityName}";

        AddInfo(L.T("Дата"), log.Date.ToString("dd.MM.yyyy HH:mm:ss"));
        AddInfo(L.T("Пользователь"), log.UserName);
        AddInfo(L.T("Компьютер"), log.Machine);
        AddInfo(L.T("Объект"), row.ObjectType + (log.EntityId is { } id ? $"  (ID {id})" : ""));
        AddInfo(L.T("Название"), log.EntityName);

        FillDetails(log.Details);

        Loaded += (_, _) => OkButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter) { DialogResult = true; e.Handled = true; }
        };
    }

    private void AddInfo(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var r = InfoGrid.RowDefinitions.Count;
        InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 3, 0, 3) };
        l.SetResourceReference(StyleProperty, "Muted");
        var v = Selectable(value);
        Grid.SetRow(l, r);
        Grid.SetRow(v, r); Grid.SetColumn(v, 1);
        InfoGrid.Children.Add(l);
        InfoGrid.Children.Add(v);
    }

    /// <summary>Формат подробностей: «Поле: было → стало; Поле2: …». Остальное — строками как есть.</summary>
    private void FillDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            AddDetailRow(Muted(L.T("Нет подробностей")), null, null);
            return;
        }

        var parts = details.Split("; ", StringSplitOptions.RemoveEmptyEntries);
        var hasChanges = parts.Any(p => p.Contains(" → "));
        if (hasChanges)
        {
            var h1 = Muted(L.T("Поле")); var h2 = Muted(L.T("Было")); var h3 = Muted(L.T("Стало"));
            AddDetailRow(h1, h2, h3);
        }
        foreach (var part in parts)
        {
            var colon = part.IndexOf(": ", StringComparison.Ordinal);
            var arrow = part.IndexOf(" → ", StringComparison.Ordinal);
            if (colon > 0 && arrow > colon)
            {
                var before = part[(colon + 2)..arrow];
                var after = part[(arrow + 3)..];
                var oldText = Selectable(before);
                oldText.SetResourceReference(ForegroundProperty, "DangerBrush");
                var newText = Selectable(after);
                newText.SetResourceReference(ForegroundProperty, "SuccessBrush");
                AddDetailRow(new TextBlock { Text = part[..colon], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 16, 4), TextWrapping = TextWrapping.Wrap },
                    oldText, newText);
            }
            else if (colon > 0)
                AddDetailRow(new TextBlock { Text = part[..colon], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 16, 4) },
                    Selectable(part[(colon + 2)..]), null, span: true);
            else
                AddDetailRow(Selectable(part), null, null, span: true);
        }
    }

    private void AddDetailRow(FrameworkElement first, FrameworkElement? before, FrameworkElement? after, bool span = false)
    {
        var r = DetailsGrid.RowDefinitions.Count;
        DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (r > 0)
        {
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            line.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, 4);
            DetailsGrid.Children.Add(line);
        }
        Grid.SetRow(first, r);
        if (before is null && after is null) Grid.SetColumnSpan(first, 4);
        DetailsGrid.Children.Add(first);
        if (before is not null)
        {
            Grid.SetRow(before, r); Grid.SetColumn(before, 1);
            if (span) Grid.SetColumnSpan(before, 3);
            DetailsGrid.Children.Add(before);
        }
        if (after is not null)
        {
            var arrow = new TextBlock { Text = "→", Margin = new Thickness(10, 4, 10, 4) };
            arrow.SetResourceReference(StyleProperty, "Muted");
            Grid.SetRow(arrow, r); Grid.SetColumn(arrow, 2);
            DetailsGrid.Children.Add(arrow);
            Grid.SetRow(after, r); Grid.SetColumn(after, 3);
            DetailsGrid.Children.Add(after);
        }
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 4, 16, 4) };
        t.SetResourceReference(StyleProperty, "Muted");
        return t;
    }

    private static TextBox Selectable(string text)
    {
        var t = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        t.SetResourceReference(StyleProperty, "SelectableText");
        return t;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var l = _row.Log;
        var text = $"{l.Date:dd.MM.yyyy HH:mm:ss}\t{l.UserName}\t{_row.ActionName}\t{_row.ObjectType}\t{l.EntityName}\n"
                   + (l.Details ?? "").Replace("; ", "\n");
        try { Clipboard.SetText(text); } catch { /* буфер занят другим приложением */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
