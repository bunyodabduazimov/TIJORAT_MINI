using System.Windows;
using MiniTrade.Desktop.Infrastructure;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

public partial class ActivationWindow : Window
{
    public ActivationWindow()
    {
        InitializeComponent();
        Host.Content = new ActivationView();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ActivationViewModel old) old.LanguageChanged -= OnLanguageChanged;
            if (e.NewValue is ActivationViewModel vm)
            {
                vm.Completed += (_, _) => { DialogResult = true; };
                vm.LanguageChanged += OnLanguageChanged;
            }
        };
    }

    /// <summary>Новый язык: тексты разметки берутся при создании, поэтому содержимое создаётся заново.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) =>
        // После выхода из обработчика выбора в списке языков — список заменяется вместе со всем содержимым
        Dispatcher.BeginInvoke(() =>
        {
            Title = L.T("TIJORAT LITE — регистрация и активация");
            Host.Content = new ActivationView();
        });
}
