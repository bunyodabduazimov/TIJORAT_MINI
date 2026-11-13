using System.IO;
using System.Text.Json;

namespace MiniTrade.Desktop.Infrastructure;

public sealed record SaleDraftLine(int ProductId, decimal Quantity, decimal Price);

/// <summary>Отложенный чек (черновик продажи): товары и параметры оформления, без движения денег и склада.</summary>
public sealed record SaleDraft(
    Guid Id, DateTime Saved, string Title, decimal Total, int ItemsCount,
    IReadOnlyList<SaleDraftLine> Lines, int? WarehouseId, int? CashRegisterId, int? CustomerId,
    decimal DiscountPercent, string? Comment);

/// <summary>Черновики продаж этого компьютера: %LocalAppData%\MiniTrade\sale-drafts.json.</summary>
public static class SaleDraftStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static string FilePath => Path.Combine(AppConfig.DataDirectory, "sale-drafts.json");

    public static List<SaleDraft> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SaleDraft>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    public static void Save(IEnumerable<SaleDraft> drafts)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            var tmp = FilePath + ".tmp"; // запись целиком, чтобы сбой не испортил файл
            File.WriteAllText(tmp, JsonSerializer.Serialize(drafts.OrderByDescending(d => d.Saved).ToList(), Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
