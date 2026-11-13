using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiniTrade.Desktop.Infrastructure;

/// <summary>
/// Подготовка фото товара: поворот по EXIF (снимки с телефона), уменьшение, прозрачность — на белый фон,
/// сохранение в JPEG. Фото — до 800 px по длинной стороне, миниатюра — до 96 px.
/// </summary>
public static class PhotoProcessor
{
    public const int PhotoSize = 800;
    public const int ThumbnailSize = 96;
    private const long MaxFileBytes = 25 * 1024 * 1024;

    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    public static string OpenFileFilter =>
        L.T("Изображения") + " (*.jpg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp";

    public static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Читает файл и возвращает (фото, миниатюра). Исключение — если файл не изображение.</summary>
    public static (byte[] Photo, byte[] Thumbnail) FromFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidOperationException(L.T("Файл слишком большой (больше 25 МБ)."));
        var source = Decode(File.ReadAllBytes(path));
        return (Encode(source, PhotoSize, 85), Encode(source, ThumbnailSize, 80));
    }

    /// <summary>Картинка для показа (без блокировки файла/потока).</summary>
    public static ImageSource? ToImage(byte[]? data, int decodeWidth = 0)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(data);
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource result = frame;
        var angle = ExifRotation(frame);
        if (angle != 0) result = new TransformedBitmap(result, new RotateTransform(angle));
        result.Freeze();
        return result;
    }

    /// <summary>Ориентация из EXIF (тег 274): 3 → 180°, 6 → 90°, 8 → 270°.</summary>
    private static double ExifRotation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("/app1/ifd/{ushort=274}")
                && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
                return orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        return 0;
    }

    private static byte[] Encode(BitmapSource source, int maxSide, int quality)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        var w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

        // Рисуем на белом фоне в 96 DPI: прозрачные PNG не становятся чёрными, размер в пикселях точный
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(source, new Rect(0, 0, w, h));
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}

/// <summary>byte[] (JPEG/PNG) → картинка для Image.Source. Параметр — ширина декодирования (для миниатюр).</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PhotoProcessor.ToImage(value as byte[], parameter is string s && int.TryParse(s, out var w) ? w : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
