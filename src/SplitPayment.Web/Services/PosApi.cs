using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitPayment.Domain.Pos;

namespace SplitPayment.Web.Services;

public sealed class PosApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class PosApi(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static Guid DemoMerchantId => DemoMerchants.DemoMerchantId;

    public Task<PreviewResponse> PreviewAsync(SaleRequest request, CancellationToken cancellationToken) =>
        SendAsync<PreviewResponse>(HttpMethod.Post, "api/v3/pos/sales/preview", request, null, cancellationToken);

    public Task<SaleDto> CreateSaleAsync(SaleRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync<SaleDto>(HttpMethod.Post, "api/v3/pos/sales", request, idempotencyKey, cancellationToken);

    public Task<SaleDto> GetSaleAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<SaleDto>(HttpMethod.Get, $"api/v3/pos/sales/{id}", null, null, cancellationToken);

    public Task<List<SaleDto>> ListSalesAsync(CancellationToken cancellationToken) =>
        SendAsync<List<SaleDto>>(HttpMethod.Get, $"api/v3/pos/merchants/{DemoMerchantId}/sales", null, null, cancellationToken);

    public Task<SaleDto> ConfirmPixAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<SaleDto>(HttpMethod.Post, $"api/v3/pos/sales/{id}/pix/confirm", null, null, cancellationToken);

    public Task<SaleDto> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<SaleDto>(HttpMethod.Post, $"api/v3/pos/sales/{id}/cancel", new CancelRequest("Cancelamento no terminal"), null, cancellationToken);

    public Task<SaleDto> RefundAsync(Guid id, long cents, CancellationToken cancellationToken) =>
        SendAsync<SaleDto>(HttpMethod.Post, $"api/v3/pos/sales/{id}/refunds", new RefundRequest(cents, "Estorno no terminal"), null, cancellationToken);

    public Task<Receipt> GetReceiptAsync(Guid id, ReceiptVia via, CancellationToken cancellationToken) =>
        SendAsync<Receipt>(HttpMethod.Get, $"api/v3/pos/sales/{id}/receipt?via={(via == ReceiptVia.Merchant ? "merchant" : "customer")}", null, null, cancellationToken);

    public Task<FinancialHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        SendAsync<FinancialHealth>(HttpMethod.Get, $"api/v3/pos/merchants/{DemoMerchantId}/health", null, null, cancellationToken);

    public async Task SeedDemoHistoryAsync(CancellationToken cancellationToken) =>
        await SendAsync<JsonElement>(HttpMethod.Post, $"api/v3/pos/merchants/{DemoMerchantId}/demo-history", null, null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var code = "api_error";
            var message = $"A API retornou {(int)response.StatusCode}.";
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                message = "Muitas requisições. Aguarde um instante.";
            }
            else
            {
                try
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
                    code = error.TryGetProperty("error", out var codeElement) ? codeElement.GetString() ?? code : code;
                    message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? message : message;
                }
                catch (JsonException)
                {
                }
            }

            throw new PosApiException(code, message);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new InvalidDataException("Resposta vazia da API.");
    }
}

public sealed record PreviewResponse(string MethodLabel, SplitDto Split, List<ReceivableDto> Receivables);
