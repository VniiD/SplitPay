namespace SplitPayment.Domain.Pos;

public sealed record CardRequest(string? Brand, string? Last4, CardEntryMode EntryMode = CardEntryMode.ChipAndPin);

public sealed record RecipientRequest(string? Name, string? TaxId, int BasisPoints);

public sealed record SaleRequest(
    Guid? MerchantId,
    string? TerminalId,
    PaymentMethod Method,
    long AmountCents,
    int Installments = 1,
    CardRequest? Card = null,
    SimulatedOutcome SimulateOutcome = SimulatedOutcome.Approve,
    TaxMode? TaxMode = null,
    IReadOnlyList<RecipientRequest>? Recipients = null);

public sealed record RefundRequest(long AmountCents, string? Reason);

public sealed record CancelRequest(string? Reason);

public sealed record MoneyDto(long Cents, string Display)
{
    public static MoneyDto From(Money money) => new(money.Cents, money.ToString());
}

public sealed record SplitLineDto(string Kind, string Beneficiary, string Description, int BasisPoints, MoneyDto Amount, bool Retained);

public sealed record ReceivableDto(int Number, int Total, DateOnly DueDate, MoneyDto Gross, MoneyDto Deductions, MoneyDto Net);

public sealed record SplitDto(
    MoneyDto Gross,
    int FeeBasisPoints,
    string TaxTreatment,
    IReadOnlyList<SplitLineDto> Lines,
    MoneyDto MerchantNet,
    MoneyDto TotalFee,
    MoneyDto TaxRetained,
    MoneyDto TaxInformational);

public sealed record SaleDto(
    Guid Id,
    long Sequence,
    string Status,
    string Method,
    string MethodLabel,
    int Installments,
    MoneyDto Gross,
    MoneyDto Refunded,
    string? DeclineReason,
    DateTimeOffset CreatedAt,
    CardInfo? Card,
    Authorization? Authorization,
    PixInfo? Pix,
    SplitDto Split,
    IReadOnlyList<ReceivableDto> Receivables,
    IReadOnlyList<RefundRecord> Refunds,
    IReadOnlyList<AuditEntry> Audit);
