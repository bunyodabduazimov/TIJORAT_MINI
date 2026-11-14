using System.Windows;
using System.Windows.Markup;
using MiniTrade.Core.Services;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Видимость элемента по праву текущего пользователя: <c>Visibility="{l:Can sales.delete}"</c>.
/// Несколько прав через «|» — достаточно любого. Страницы создаются после входа, поэтому значение вычисляется один раз.
/// </summary>
[MarkupExtensionReturnType(typeof(Visibility))]
public sealed class CanExtension(string permission) : MarkupExtension
{
    public string Permission { get; set; } = permission;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Permission.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(Access.Can)
            ? Visibility.Visible : Visibility.Collapsed;
}
