using System.Text.Json;
using MongoDB.Driver;
using SplitPayment.Domain.Events;
using SplitPayment.Domain.Pos;

namespace SplitPayment.InvoiceApi.Pos;

/// <summary>Publishes captured POS sales to the existing outbox → Kafka → Engine/CGIBS ledger pipeline.</summary>
public interface ISaleLedgerSink
{
    Task PublishCapturedAsync(Sale sale, Merchant merchant, CancellationToken cancellationToken);
}

public sealed class OutboxSaleLedgerSink(
    IMongoCollection<InvoiceOutboxDocument> outbox,
    ILogger<OutboxSaleLedgerSink> logger) : ISaleLedgerSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishCapturedAsync(Sale sale, Merchant merchant, CancellationToken cancellationToken)
    {
        var retained = sale.Split.TaxRetainedAtSale;
        var ibs = sale.Split.Lines.First(line => line.Kind == SplitLineKind.TaxIbs);
        var cbs = sale.Split.Lines.First(line => line.Kind == SplitLineKind.TaxCbs);
        var invoice = new InvoiceCreatedEvent(
            sale.Id,
            merchant.TaxId,
            "00000000000",
            sale.Gross.Cents / 100m,
            retained ? ibs.BasisPoints / 100m : 0m,
            retained ? cbs.BasisPoints / 100m : 0m,
            false,
            (sale.CapturedAt ?? sale.CreatedAt).UtcDateTime);

        var document = new InvoiceOutboxDocument
        {
            Id = $"pos-sale-{sale.Id:N}",
            RequestHash = sale.Id.ToString("N").ToUpperInvariant(),
            InvoiceId = invoice.InvoiceId.ToString("D"),
            EventJson = JsonSerializer.Serialize(invoice, JsonOptions),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await outbox.InsertOneAsync(document, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            logger.LogDebug("POS sale {SaleId} was already queued for the ledger.", sale.Id);
        }
    }
}

public sealed class NoOpSaleLedgerSink : ISaleLedgerSink
{
    public Task PublishCapturedAsync(Sale sale, Merchant merchant, CancellationToken cancellationToken) => Task.CompletedTask;
}
