using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using SplitPayment.Domain.Pos;
using SplitPayment.Domain.Validation;

namespace SplitPayment.InvoiceApi.Pos;

public sealed record PreviewResult(SplitResult Split, IReadOnlyList<Receivable> Receivables, string MethodLabel);

public sealed class PosService(PosStore store, ISaleLedgerSink ledger, TimeProvider clock, ILogger<PosService> logger)
{
    public static readonly TimeSpan BrasiliaOffset = TimeSpan.FromHours(-3);
    private const int DefaultSimulatedIbsBasisPoints = 1770;
    private const int DefaultSimulatedCbsBasisPoints = 880;
    private static readonly TimeSpan PixLifetime = TimeSpan.FromMinutes(15);

    public DateTimeOffset Now => clock.GetUtcNow();

    public PreviewResult Preview(SaleRequest request)
    {
        var merchant = ResolveMerchant(request);
        var (split, receivables) = BuildSplit(request, merchant, Now);
        return new PreviewResult(split, receivables, request.Method.Label(request.Installments));
    }

    public async Task<(Sale Sale, bool Replayed)> CreateSaleAsync(
        SaleRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken,
        DateTimeOffset? at = null)
    {
        var merchant = ResolveMerchant(request);
        var terminal = ResolveTerminal(request, merchant);
        var requestHash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(request with { MerchantId = merchant.Id, TerminalId = terminal.Id })));

        Sale sale;
        lock (store.Gate)
        {
            var existing = store.GetIdempotent(merchant.Id, idempotencyKey);
            if (existing is not null)
            {
                if (existing.RequestHash != requestHash)
                {
                    throw new DomainRuleException("idempotency_key_reused_with_different_request", "Idempotency key already used with a different request.");
                }

                return (store.GetSale(existing.SaleId)!, true);
            }

            var now = at ?? Now;
            var (split, receivables) = BuildSplit(request, merchant, now);
            sale = new Sale(Guid.NewGuid(), merchant.Id, terminal.Id, request.Method, request.Installments,
                new Money(request.AmountCents), split, receivables, now, store.NextSequence());

            if (request.Method == PaymentMethod.Pix)
            {
                var txid = $"SP{sale.Sequence:D8}{sale.Id.ToString("N")[..10].ToUpperInvariant()}";
                var payload = PixBrCode.Build(merchant.PixKey, merchant.TradeName, merchant.City, sale.Gross, txid);
                sale.AttachPix(new PixInfo(txid, payload, now + PixLifetime, null, null));
            }
            else
            {
                var card = CardAuthorizer.NormalizeCard(request.Card?.Brand, request.Card?.Last4, request.Card?.EntryMode ?? CardEntryMode.ChipAndPin, request.Method);
                sale.ApplyCardAuthorization(card, CardAuthorizer.Authorize(request.SimulateOutcome, sale.Sequence), now);
            }

            store.Add(sale);
            store.TryRegisterIdempotency(merchant.Id, idempotencyKey, new IdempotentSale(requestHash, sale.Id));
        }

        if (sale.Status == SaleStatus.Captured)
        {
            await PublishAsync(sale, merchant, cancellationToken);
        }

        return (sale, false);
    }

    public async Task<Sale> ConfirmPixAsync(Guid saleId, string? payerName, CancellationToken cancellationToken)
    {
        var sale = Require(saleId);
        ConfirmPix(sale, payerName, Now);
        await PublishAsync(sale, MerchantOf(sale), cancellationToken);
        return sale;
    }

    private void ConfirmPix(Sale sale, string? payerName, DateTimeOffset now)
    {
        lock (store.Gate)
        {
            var endToEnd = $"E{MerchantOf(sale).TaxId[..8]}{now.UtcDateTime:yyyyMMddHHmm}{RandomNumberGenerator.GetHexString(11)}";
            sale.ConfirmPix(endToEnd, string.IsNullOrWhiteSpace(payerName) ? "Cliente Simulado" : payerName.Trim(), now);
        }
    }

    public Sale Cancel(Guid saleId, string? reason)
    {
        var sale = Require(saleId);
        lock (store.Gate)
        {
            sale.Cancel(Now, Reason(reason, "Cancelamento solicitado"), BrasiliaOffset);
        }

        return sale;
    }

    public Sale Refund(Guid saleId, RefundRequest request)
    {
        var sale = Require(saleId);
        lock (store.Gate)
        {
            sale.Refund(new Money(request.AmountCents), Reason(request.Reason, "Estorno solicitado"), Now);
        }

        return sale;
    }

    public Sale? Find(Guid saleId)
    {
        var sale = store.GetSale(saleId);
        if (sale is not null)
        {
            lock (store.Gate)
            {
                sale.ExpirePixIfDue(Now);
            }
        }

        return sale;
    }

    public IReadOnlyList<Sale> List(Guid merchantId)
    {
        var sales = store.SalesOf(merchantId);
        lock (store.Gate)
        {
            foreach (var sale in sales)
            {
                sale.ExpirePixIfDue(Now);
            }
        }

        return sales;
    }

    public FinancialHealth Health(Guid merchantId)
    {
        var sales = List(merchantId);
        lock (store.Gate)
        {
            return FinancialHealthCalculator.Calculate(sales, Now, BrasiliaOffset);
        }
    }

    public Merchant MerchantOf(Sale sale) => store.GetMerchant(sale.MerchantId)!;

    public Terminal TerminalOf(Sale sale) => store.GetTerminal(sale.TerminalId)!;

    public async Task<int> SeedDemoHistoryAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        var merchant = store.GetMerchant(merchantId) ?? throw new DomainRuleException("merchant_not_found", "Merchant not found.");
        if (store.SalesOf(merchantId).Count > 0)
        {
            return 0;
        }

        var random = new Random(2026);
        var methods = new[] { PaymentMethod.Pix, PaymentMethod.Debit, PaymentMethod.Credit, PaymentMethod.Credit, PaymentMethod.MealVoucher, PaymentMethod.FoodVoucher };
        var brands = new[] { "VISA", "MASTERCARD", "ELO" };
        var created = 0;
        var terminal = store.TerminalsOf(merchantId).First();
        for (var day = 27; day >= 0; day--)
        {
            var salesToday = random.Next(2, 5);
            for (var index = 0; index < salesToday; index++)
            {
                var method = methods[random.Next(methods.Length)];
                var installments = method == PaymentMethod.Credit && random.Next(3) == 0 ? random.Next(2, 7) : 1;
                var at = Now.AddDays(-day).AddHours(-random.Next(0, 8));
                var request = new SaleRequest(
                    merchantId, terminal.Id, method, random.Next(1500, 32000), installments,
                    method == PaymentMethod.Pix ? null : new CardRequest(method is PaymentMethod.MealVoucher or PaymentMethod.FoodVoucher ? "ALELO" : brands[random.Next(brands.Length)], random.Next(1000, 9999).ToString(), CardEntryMode.Contactless),
                    random.Next(14) == 0 ? SimulatedOutcome.InsufficientFunds : SimulatedOutcome.Approve);
                var (sale, _) = await CreateSaleAsync(request, $"seed-{day}-{index}", cancellationToken, at);
                if (sale.Status == SaleStatus.PendingPayment)
                {
                    ConfirmPix(sale, "Cliente Demo", at.AddMinutes(1));
                    await PublishAsync(sale, merchant, cancellationToken);
                }

                if (sale.Status == SaleStatus.Captured && random.Next(25) == 0)
                {
                    sale.Refund(new Money(sale.Gross.Cents / 2), "Devolução parcial (demo)", at.AddHours(2));
                }

                created++;
            }
        }

        return created;
    }

    private async Task PublishAsync(Sale sale, Merchant merchant, CancellationToken cancellationToken)
    {
        try
        {
            await ledger.PublishCapturedAsync(sale, merchant, cancellationToken);
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException)
        {
            logger.LogWarning(exception, "Ledger publication deferred for POS sale {SaleId}.", sale.Id);
            lock (store.Gate)
            {
                sale.NoteLedgerDeferred(Now);
            }
        }
    }

    private (SplitResult Split, IReadOnlyList<Receivable> Receivables) BuildSplit(SaleRequest request, Merchant merchant, DateTimeOffset now)
    {
        if (!Enum.IsDefined(request.Method))
        {
            throw new DomainRuleException("method_invalid", "Unknown payment method.");
        }

        if (request.AmountCents is <= 0 or > 99_999_999_99)
        {
            throw new DomainRuleException("amount_invalid", "Amount must be between R$ 0,01 and R$ 99.999.999,99.");
        }

        var recipients = (request.Recipients ?? []).Select(item =>
        {
            if (!string.IsNullOrWhiteSpace(item.TaxId) && !TaxDocumentValidator.IsValid(item.TaxId))
            {
                throw new DomainRuleException("recipient_tax_id_invalid", "Recipient tax id is invalid.");
            }

            return new SplitRecipient(item.Name ?? string.Empty, item.TaxId ?? string.Empty, item.BasisPoints);
        }).ToList();

        var taxMode = request.TaxMode ?? merchant.TaxMode;
        try
        {
            var split = SplitEngine.Calculate(
                new SplitInput(new Money(request.AmountCents), request.Method, request.Installments, taxMode,
                    DefaultSimulatedIbsBasisPoints, DefaultSimulatedCbsBasisPoints, recipients),
                merchant.Fees);
            var deductions = split.Gross - split.MerchantNet;
            var schedule = ReceivableScheduler.Build(
                request.Method, request.Installments, split.Gross, deductions,
                DateOnly.FromDateTime(now.ToOffset(BrasiliaOffset).DateTime));
            return (split, schedule);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new DomainRuleException("split_invalid", exception.Message);
        }
    }

    private Merchant ResolveMerchant(SaleRequest request) =>
        store.GetMerchant(request.MerchantId ?? DemoMerchants.DemoMerchantId)
            ?? throw new DomainRuleException("merchant_not_found", "Merchant not found.");

    private Terminal ResolveTerminal(SaleRequest request, Merchant merchant)
    {
        var terminal = store.GetTerminal(request.TerminalId ?? DemoMerchants.DemoTerminal.Id);
        if (terminal is null || terminal.MerchantId != merchant.Id || !terminal.Active)
        {
            throw new DomainRuleException("terminal_invalid", "Terminal not found, inactive, or not linked to the merchant.");
        }

        return terminal;
    }

    private Sale Require(Guid id) => Find(id) ?? throw new DomainRuleException("sale_not_found", "Sale not found.");

    private static string Reason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim()[..Math.Min(reason.Trim().Length, 120)];
}
