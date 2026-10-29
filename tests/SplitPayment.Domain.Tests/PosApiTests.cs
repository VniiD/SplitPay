using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SplitPayment.Domain.Pos;
using SplitPayment.InvoiceApi.Pos;

namespace SplitPayment.Domain.Tests;

public sealed class RecordingLedgerSink : ISaleLedgerSink
{
    public List<Guid> Published { get; } = [];

    public Task PublishCapturedAsync(Sale sale, Merchant merchant, CancellationToken cancellationToken)
    {
        lock (Published)
        {
            Published.Add(sale.Id);
        }

        return Task.CompletedTask;
    }
}

public sealed class PosApiFactory : InvoiceApiApplicationFactory
{
    public RecordingLedgerSink Ledger { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISaleLedgerSink>();
            services.AddSingleton<ISaleLedgerSink>(Ledger);
        });
    }
}

public sealed class PosApiTests(PosApiFactory factory) : IClassFixture<PosApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Pos_RequiresApiKey()
    {
        using var response = await _client.GetAsync("/api/v3/pos/catalog");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreditInstallmentSale_ReturnsSplitReceivablesAndPublishesToLedger()
    {
        using var response = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Credit, 25000, 3), "pos-credit-0001");
        var sale = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Captured", sale.GetProperty("status").GetString());
        Assert.Equal(3, sale.GetProperty("receivables").GetArrayLength());
        Assert.Equal(25000, sale.GetProperty("receivables").EnumerateArray().Sum(item => item.GetProperty("gross").GetProperty("cents").GetInt64()));
        Assert.Equal(25000, sale.GetProperty("split").GetProperty("lines").EnumerateArray()
            .Where(line => line.GetProperty("retained").GetBoolean())
            .Sum(line => line.GetProperty("amount").GetProperty("cents").GetInt64()));
        Assert.Contains(Guid.Parse(sale.GetProperty("id").GetString()!), factory.Ledger.Published);
    }

    [Fact]
    public async Task CreateSale_IsIdempotentAndRejectsReuseWithDifferentBody()
    {
        var body = CardSale(PaymentMethod.Debit, 5000, 1);
        using var first = await Send(HttpMethod.Post, "/api/v3/pos/sales", body, "pos-idem-00001");
        using var replay = await Send(HttpMethod.Post, "/api/v3/pos/sales", body, "pos-idem-00001");
        using var conflict = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Debit, 5001, 1), "pos-idem-00001");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString(),
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task CreateSale_RequiresIdempotencyKeyAndValidAmount()
    {
        using var missing = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Debit, 5000, 1), null);
        using var invalid = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Debit, 0, 1), "pos-bad-amount-1");
        using var badInstallments = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Debit, 9000, 3), "pos-bad-inst-001");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badInstallments.StatusCode);
    }

    [Fact]
    public async Task DeclinedCard_ReturnsDeclinedReceiptWithoutLedgerPublication()
    {
        var body = CardSale(PaymentMethod.Credit, 8000, 1) with { SimulateOutcome = SimulatedOutcome.InsufficientFunds };
        using var response = await Send(HttpMethod.Post, "/api/v3/pos/sales", body, "pos-declined-001");
        var sale = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Declined", sale.GetProperty("status").GetString());
        Assert.DoesNotContain(Guid.Parse(sale.GetProperty("id").GetString()!), factory.Ledger.Published);
        using var receipt = await Send(HttpMethod.Get, $"/api/v3/pos/sales/{sale.GetProperty("id").GetString()}/receipt?format=text", null, null);
        Assert.Contains("TRANSAÇÃO NEGADA", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PixSale_GeneratesValidBrCodeAndCapturesOnConfirmation()
    {
        var body = new SaleRequest(null, null, PaymentMethod.Pix, 4590);
        using var created = await Send(HttpMethod.Post, "/api/v3/pos/sales", body, "pos-pix-000001");
        var sale = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = sale.GetProperty("id").GetString();

        Assert.Equal("PendingPayment", sale.GetProperty("status").GetString());
        Assert.True(PixBrCode.IsValid(sale.GetProperty("pix").GetProperty("copyAndPaste").GetString()!));

        using var confirmed = await Send(HttpMethod.Post, $"/api/v3/pos/sales/{id}/pix/confirm", null, null);
        var after = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Captured", after.GetProperty("status").GetString());
        Assert.StartsWith("E", after.GetProperty("pix").GetProperty("endToEndId").GetString(), StringComparison.Ordinal);

        using var cancel = await Send(HttpMethod.Post, $"/api/v3/pos/sales/{id}/cancel", new CancelRequest("x"), null);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
    }

    [Fact]
    public async Task Refund_UpdatesStatusAndRejectsExcess()
    {
        using var created = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Debit, 10000, 1), "pos-refund-0001");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        using var partial = await Send(HttpMethod.Post, $"/api/v3/pos/sales/{id}/refunds", new RefundRequest(4000, "Item devolvido"), null);
        using var excess = await Send(HttpMethod.Post, $"/api/v3/pos/sales/{id}/refunds", new RefundRequest(6001, "Excesso"), null);

        Assert.Equal("PartiallyRefunded", (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excess.StatusCode);
    }

    [Fact]
    public async Task Preview_ShowsSplitWithoutCreatingSale()
    {
        var body = CardSale(PaymentMethod.Credit, 100000, 1) with
        {
            TaxMode = TaxMode.SimulatedRetention,
            Recipients = [new RecipientRequest("Fornecedor A", "11222333000181", 1500)]
        };
        using var response = await Send(HttpMethod.Post, "/api/v3/pos/sales/preview", body, null);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("SimulatedRetention", json.GetProperty("split").GetProperty("taxTreatment").GetString());
        Assert.Equal(100000 - 17700 - 8800 - 3490 - 15000, json.GetProperty("split").GetProperty("merchantNet").GetProperty("cents").GetInt64());
    }

    [Fact]
    public async Task Receipt_CustomerCopyHidesFeesAndMerchantCopyShowsSplit()
    {
        using var created = await Send(HttpMethod.Post, "/api/v3/pos/sales", CardSale(PaymentMethod.Credit, 12000, 2), "pos-receipt-001");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var customer = await (await Send(HttpMethod.Get, $"/api/v3/pos/sales/{id}/receipt?format=text", null, null)).Content.ReadAsStringAsync();
        var merchant = await (await Send(HttpMethod.Get, $"/api/v3/pos/sales/{id}/receipt?via=merchant&format=text", null, null)).Content.ReadAsStringAsync();

        Assert.Contains("VIA CLIENTE", customer, StringComparison.Ordinal);
        Assert.DoesNotContain("Taxa de", customer, StringComparison.Ordinal);
        Assert.Contains("VIA ESTABELECIMENTO", merchant, StringComparison.Ordinal);
        Assert.Contains("SPLIT / REPASSE", merchant, StringComparison.Ordinal);
        Assert.Contains("2/2", merchant, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_AfterDemoHistory_ReturnsIndicators()
    {
        using var seeded = await Send(HttpMethod.Post, $"/api/v3/pos/merchants/{DemoMerchants.DemoMerchantId}/demo-history", null, null);
        using var health = await Send(HttpMethod.Get, $"/api/v3/pos/merchants/{DemoMerchants.DemoMerchantId}/health", null, null);
        var json = await health.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.InRange(json.GetProperty("score").GetInt32(), 0, 100);
        Assert.True(json.GetProperty("grossMonth").GetProperty("cents").GetInt64() > 0);
        Assert.True(json.GetProperty("receivablesTotal").GetProperty("cents").GetInt64() > 0);
    }

    [Fact]
    public async Task OpenApi_DescribesPosEndpoints()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/v3/pos/sales", json, StringComparison.Ordinal);
        Assert.Contains("/api/v3/pos/merchants/{merchantId}/health", json, StringComparison.Ordinal);
    }

    private static SaleRequest CardSale(PaymentMethod method, long cents, int installments) =>
        new(null, null, method, cents, installments, new CardRequest("VISA", "4242"));

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-API-Key", InvoiceApiApplicationFactory.TestApiKey);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
        }

        return await _client.SendAsync(request);
    }
}
