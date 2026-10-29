using System.Collections.Concurrent;
using SplitPayment.Domain.Pos;

namespace SplitPayment.InvoiceApi.Pos;

public sealed record IdempotentSale(string RequestHash, Guid SaleId);

/// <summary>Thread-safe in-memory store for the POS module (demo persistence; replace with a durable store for production).</summary>
public sealed class PosStore
{
    private readonly ConcurrentDictionary<Guid, Merchant> _merchants = new();
    private readonly ConcurrentDictionary<string, Terminal> _terminals = new();
    private readonly ConcurrentDictionary<Guid, Sale> _sales = new();
    private readonly ConcurrentDictionary<string, IdempotentSale> _idempotency = new();
    private long _sequence;

    public PosStore()
    {
        _merchants[DemoMerchants.Demo.Id] = DemoMerchants.Demo;
        _terminals[DemoMerchants.DemoTerminal.Id] = DemoMerchants.DemoTerminal;
    }

    public object Gate { get; } = new();

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public Merchant? GetMerchant(Guid id) => _merchants.GetValueOrDefault(id);

    public IReadOnlyCollection<Merchant> Merchants => _merchants.Values.ToList();

    public Terminal? GetTerminal(string id) => _terminals.GetValueOrDefault(id);

    public IReadOnlyCollection<Terminal> TerminalsOf(Guid merchantId) =>
        _terminals.Values.Where(terminal => terminal.MerchantId == merchantId).ToList();

    public void Add(Sale sale) => _sales[sale.Id] = sale;

    public Sale? GetSale(Guid id) => _sales.GetValueOrDefault(id);

    public IReadOnlyList<Sale> SalesOf(Guid merchantId) =>
        _sales.Values.Where(sale => sale.MerchantId == merchantId).OrderByDescending(sale => sale.Sequence).ToList();

    public IdempotentSale? GetIdempotent(Guid merchantId, string key) => _idempotency.GetValueOrDefault($"{merchantId:N}:{key}");

    public bool TryRegisterIdempotency(Guid merchantId, string key, IdempotentSale entry) =>
        _idempotency.TryAdd($"{merchantId:N}:{key}", entry);
}
