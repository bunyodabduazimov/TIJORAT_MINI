using MiniTrade.Desktop.Infrastructure;
using System.Net.Http;
using System.Net.Http.Json;
using MiniTrade.Licensing;

namespace MiniTrade.Desktop.Licensing;

/// <summary>HTTPS-клиент API лицензирования.</summary>
public sealed class LicenseClient(HttpClient http)
{
    public Task<LicenseResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        PostAsync("api/v1/license/register", request, ct);

    public Task<LicenseResponse> ActivateAsync(ActivateRequest request, CancellationToken ct = default) =>
        PostAsync("api/v1/license/activate", request, ct);

    public Task<LicenseResponse> ValidateAsync(ValidateRequest request, CancellationToken ct = default) =>
        PostAsync("api/v1/license/validate", request, ct);

    private async Task<LicenseResponse> PostAsync<T>(string url, T body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, ct);
        if ((int)response.StatusCode == 429)
            return new LicenseResponse(false, L.T("Слишком много попыток. Повторите через минуту."), ErrorCode: "rate_limited");
        try
        {
            var result = await response.Content.ReadFromJsonAsync<LicenseResponse>(ct);
            if (result is not null) return result;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { }
        throw new HttpRequestException(L.F("Сервер лицензий вернул ошибку {0}.", (int)response.StatusCode));
    }
}
