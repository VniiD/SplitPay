using Microsoft.AspNetCore.Mvc;
using SplitPayment.Domain.Pos;

namespace SplitPayment.InvoiceApi.Pos;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v3/pos").RequireRateLimiting("pos").WithTags("POS");

        group.MapGet("/merchants", (PosStore store) => Results.Ok(store.Merchants.Select(merchant => new
        {
            merchant.Id,
            merchant.TradeName,
            merchant.LegalName,
            merchant.TaxId,
            merchant.City,
            merchant.State,
            taxMode = merchant.TaxMode.ToString(),
            terminals = store.TerminalsOf(merchant.Id).Select(terminal => new { terminal.Id, terminal.Model, terminal.Active })
        })))
            .WithSummary("List demo merchants and their terminals");

        group.MapGet("/catalog", () => Results.Ok(new
        {
            methods = Enum.GetValues<PaymentMethod>().Select(method => new { id = method.ToString(), label = method.Label(1) }),
            brands = CardAuthorizer.Brands,
            maxInstallments = FeeSchedule.Demo.MaxInstallments,
            fees = new
            {
                pixBasisPoints = FeeSchedule.Demo.PixBasisPoints,
                debitBasisPoints = FeeSchedule.Demo.DebitBasisPoints,
                creditSingleBasisPoints = FeeSchedule.Demo.CreditSingleBasisPoints,
                credit2To6BasisPoints = FeeSchedule.Demo.CreditInstallments2To6BasisPoints,
                credit7To12BasisPoints = FeeSchedule.Demo.CreditInstallments7To12BasisPoints,
                voucherBasisPoints = FeeSchedule.Demo.VoucherBasisPoints
            },
            disclaimer = "Taxas ilustrativas de demonstração; não representam tabela real de adquirente."
        })).WithSummary("Payment methods, brands and demo fee table");

        group.MapPost("/sales/preview", (SaleRequest request, PosService service) => Handle(() =>
        {
            var preview = service.Preview(request);
            return Results.Ok(new
            {
                methodLabel = preview.MethodLabel,
                split = ToDto(preview.Split),
                receivables = preview.Receivables.Select(ToDto)
            });
        })).WithSummary("Simulate the split and settlement schedule without creating a sale");

        group.MapPost("/sales", async (
            SaleRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            PosService service,
            CancellationToken cancellationToken) => await HandleAsync(async () =>
        {
            if (!IsValidIdempotencyKey(idempotencyKey))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Idempotency-Key"] = ["Provide 8 to 128 ASCII letters, digits, '.', '_' or '-'."]
                });
            }

            var (sale, replayed) = await service.CreateSaleAsync(request, idempotencyKey!, cancellationToken);
            return Results.Json(ToDto(sale), statusCode: replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        })).WithSummary("Create a sale: card authorization or Pix charge, with split and receivables");

        group.MapGet("/sales/{saleId:guid}", (Guid saleId, PosService service) => Handle(() =>
            service.Find(saleId) is { } sale ? Results.Ok(ToDto(sale)) : NotFound()))
            .WithSummary("Get a sale");

        group.MapGet("/merchants/{merchantId:guid}/sales", (Guid merchantId, PosService service) =>
            Results.Ok(service.List(merchantId).Take(100).Select(ToDto)))
            .WithSummary("List the latest sales of a merchant");

        group.MapPost("/sales/{saleId:guid}/pix/confirm", async (
            Guid saleId,
            PosService service,
            CancellationToken cancellationToken) => await HandleAsync(async () =>
                Results.Ok(ToDto(await service.ConfirmPixAsync(saleId, null, cancellationToken)))))
            .WithSummary("Simulate the payer confirming a Pix charge");

        group.MapPost("/sales/{saleId:guid}/cancel", (Guid saleId, CancelRequest? request, PosService service) => Handle(() =>
            Results.Ok(ToDto(service.Cancel(saleId, request?.Reason)))))
            .WithSummary("Cancel a captured card sale on the same day");

        group.MapPost("/sales/{saleId:guid}/refunds", (Guid saleId, RefundRequest request, PosService service) => Handle(() =>
            Results.Ok(ToDto(service.Refund(saleId, request)))))
            .WithSummary("Refund (full or partial) a captured sale");

        group.MapGet("/sales/{saleId:guid}/receipt", (
            Guid saleId,
            PosService service,
            [FromQuery] string? via,
            [FromQuery] string? format) => Handle(() =>
        {
            var sale = service.Find(saleId);
            if (sale is null)
            {
                return NotFound();
            }

            var receiptVia = string.Equals(via, "merchant", StringComparison.OrdinalIgnoreCase) ? ReceiptVia.Merchant : ReceiptVia.Customer;
            var receipt = ReceiptBuilder.Build(sale, service.MerchantOf(sale), service.TerminalOf(sale), receiptVia, PosService.BrasiliaOffset);
            return string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(ReceiptBuilder.RenderText(receipt), "text/plain", System.Text.Encoding.UTF8)
                : Results.Ok(receipt);
        })).WithSummary("Receipt (comprovante) for the customer or merchant copy, as JSON or 40-column text");

        group.MapGet("/merchants/{merchantId:guid}/health", (Guid merchantId, PosService service) => Handle(() =>
            Results.Ok(service.Health(merchantId))))
            .WithSummary("Micro financial manager: health score, revenue, fees, taxes and receivables");

        group.MapPost("/merchants/{merchantId:guid}/demo-history", async (
            Guid merchantId,
            PosService service) => await HandleAsync(async () =>
                Results.Ok(new { created = await service.SeedDemoHistoryAsync(merchantId, CancellationToken.None) })))
            .WithSummary("Seed 28 days of deterministic demo sales (only when the merchant has no sales)");

        return app;
    }

    private static IResult NotFound() => Results.NotFound(new { error = "sale_not_found" });

    private static IResult Handle(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (DomainRuleException exception)
        {
            return ToResult(exception);
        }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainRuleException exception)
        {
            return ToResult(exception);
        }
    }

    private static IResult ToResult(DomainRuleException exception)
    {
        var status = exception.Code switch
        {
            "sale_not_found" or "merchant_not_found" => StatusCodes.Status404NotFound,
            "idempotency_key_reused_with_different_request" => StatusCodes.Status409Conflict,
            var code when code.EndsWith("_invalid_state", StringComparison.Ordinal) || code is "cancel_window_closed" or "pix_expired" or "pix_cannot_be_cancelled"
                => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity
        };

        return Results.Json(new { error = exception.Code, message = exception.Message }, statusCode: status);
    }

    private static bool IsValidIdempotencyKey(string? key) =>
        key is { Length: >= 8 and <= 128 } && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static SaleDto ToDto(Sale sale) => new(
        sale.Id,
        sale.Sequence,
        sale.Status.ToString(),
        sale.Method.ToString(),
        sale.Method.Label(sale.Installments),
        sale.Installments,
        MoneyDto.From(sale.Gross),
        MoneyDto.From(sale.RefundedTotal),
        sale.DeclineReason,
        sale.CreatedAt,
        sale.Card,
        sale.Authorization,
        sale.Pix,
        ToDto(sale.Split),
        sale.Receivables.Select(ToDto).ToList(),
        sale.Refunds,
        sale.Audit);

    private static SplitDto ToDto(SplitResult split) => new(
        MoneyDto.From(split.Gross),
        split.FeeBasisPoints,
        split.TaxRetainedAtSale ? "SimulatedRetention" : "Informative2026",
        split.Lines.Select(line => new SplitLineDto(line.Kind.ToString(), line.Beneficiary, line.Description, line.BasisPoints, MoneyDto.From(line.Amount), line.Retained)).ToList(),
        MoneyDto.From(split.MerchantNet),
        MoneyDto.From(split.TotalFee),
        MoneyDto.From(split.TaxRetained),
        MoneyDto.From(split.TaxInformational));

    private static ReceivableDto ToDto(Receivable receivable) => new(
        receivable.Number, receivable.Total, receivable.DueDate,
        MoneyDto.From(receivable.Gross), MoneyDto.From(receivable.Fee), MoneyDto.From(receivable.Net));
}
