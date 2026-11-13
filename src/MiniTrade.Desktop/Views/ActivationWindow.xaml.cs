using System.Windows;
using MiniTrade.Desktop.ViewModels;

namespace MiniTrade.Desktop.Views;

public partial class ActivationWindow : Window
{
    public ActivationWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ActivationViewModel vm) vm.Completed += (_, _) => { DialogResult = true; };
        };
    }
}
