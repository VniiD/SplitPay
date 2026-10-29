namespace SplitPayment.Domain.Pos;

public sealed record Merchant(
    Guid Id,
    string LegalName,
    string TradeName,
    string TaxId,
    string Mcc,
    string AddressLine,
    string City,
    string State,
    string PixKey,
    TaxMode TaxMode,
    FeeSchedule Fees);

public sealed record Terminal(
    string Id,
    Guid MerchantId,
    string SerialNumber,
    string Model,
    bool Active);

public static class DemoMerchants
{
    public static readonly Guid DemoMerchantId = Guid.Parse("7f1c9a52-1d0e-4a6b-9a3b-5c2d8e4f1a01");

    public static Merchant Demo { get; } = new(
        DemoMerchantId,
        "Mercado Boa Vista Comércio de Alimentos LTDA",
        "Mercado Boa Vista",
        "11222333000181",
        "5411",
        "Av. Exemplo, 1000 - Centro",
        "São Paulo",
        "SP",
        "5f2d9c3e-7a41-4c1e-8b0a-0d6e9b1c2a77",
        TaxMode.Informative2026,
        FeeSchedule.Demo);

    public static Terminal DemoTerminal { get; } = new("TERM-0001", DemoMerchantId, "SIM-2026-000001", "SplitPay Virtual POS", true);
}
