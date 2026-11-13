using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>Окно-модалка (рамка без системного заголовка, поля под тень): поверх неё затемнение не рисуется.</summary>
public interface IModalWindow { }

/// <summary>Показ модальных окон приложения: владелец — активное окно, основное окно под модалкой затемняется.</summary>
public static class ModalHost
{
    public static bool? ShowDialog(Window window)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != window)
                    ?? (Application.Current.MainWindow is { IsVisible: true } main && main != window ? main : null);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Вложенная модалка (поверх модалки) не затемняет прозрачные поля нижней
        var dim = owner is not null and not IModalWindow ? DimAdorner.Add(owner) : null;
        try { return window.ShowDialog(); }
        finally { dim?.Remove(); }
    }

    /// <summary>Полупрозрачная вуаль поверх содержимого окна-владельца.</summary>
    private sealed class DimAdorner : Adorner
    {
        private static readonly Brush Veil = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0x10, 0x16, 0x28)));
        private readonly AdornerLayer _layer;

        private DimAdorner(UIElement element, AdornerLayer layer) : base(element)
        {
            _layer = layer;
            IsHitTestVisible = true; // гасит клики по окну под модалкой
        }

        public static DimAdorner? Add(Window owner)
        {
            if (owner.Content is not UIElement content || AdornerLayer.GetAdornerLayer(content) is not { } layer) return null;
            var adorner = new DimAdorner(content, layer);
            layer.Add(adorner);
            return adorner;
        }

        public void Remove() => _layer.Remove(this);

        protected override void OnRender(DrawingContext dc) =>
            dc.DrawRectangle(Veil, null, new Rect(new Point(-50, -50),
                new Size(AdornedElement.RenderSize.Width + 100, AdornedElement.RenderSize.Height + 100)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
