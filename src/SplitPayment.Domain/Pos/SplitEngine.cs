namespace SplitPayment.Domain.Pos;

public enum TaxMode
{
    /// <summary>2026 test phase: IBS/CBS (0.1% / 0.9%) are shown for information only and are not retained.</summary>
    Informative2026 = 1,

    /// <summary>Simulates the full split payment: IBS/CBS are retained at the sale and routed to the tax authorities.</summary>
    SimulatedRetention = 2
}

public enum SplitLineKind
{
    TaxIbs = 1,
    TaxCbs = 2,
    AcquirerFee = 3,
    Recipient = 4,
    Merchant = 5
}

public sealed record SplitLine(
    SplitLineKind Kind,
    string Beneficiary,
    string Description,
    int BasisPoints,
    Money Amount,
    bool Retained);

public sealed record SplitRecipient(string Name, string TaxId, int BasisPoints);

public sealed record SplitInput(
    Money Gross,
    PaymentMethod Method,
    int Installments,
    TaxMode TaxMode,
    int IbsBasisPoints = 0,
    int CbsBasisPoints = 0,
    IReadOnlyList<SplitRecipient>? Recipients = null);

public sealed record SplitResult(
    Money Gross,
    int FeeBasisPoints,
    IReadOnlyList<SplitLine> Lines,
    Money MerchantNet,
    Money TotalFee,
    Money TaxRetained,
    Money TaxInformational,
    bool TaxRetainedAtSale)
{
    public Money TotalDistributed => Lines.Where(line => line.Retained).Aggregate(Money.Zero, (sum, line) => sum + line.Amount);
}

public static class SplitEngine
{
    public const int InformativeIbsBasisPoints = 10;
    public const int InformativeCbsBasisPoints = 90;
    public const int MaxRecipients = 5;

    public static SplitResult Calculate(SplitInput input, FeeSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(schedule);

        if (input.Gross.Cents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Gross amount must be positive.");
        }

        schedule.ValidateInstallments(input.Method, input.Installments, input.Gross);

        var retained = input.TaxMode == TaxMode.SimulatedRetention;
        var ibsBps = retained ? input.IbsBasisPoints : InformativeIbsBasisPoints;
        var cbsBps = retained ? input.CbsBasisPoints : InformativeCbsBasisPoints;
        if (ibsBps is < 0 or > 10_000 || cbsBps is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Tax rates must be between 0 and 100%.");
        }

        var recipients = input.Recipients ?? [];
        if (recipients.Count > MaxRecipients)
        {
            throw new ArgumentOutOfRangeException(nameof(input), $"At most {MaxRecipients} additional recipients are supported.");
        }

        if (recipients.Any(recipient => recipient.BasisPoints is <= 0 or > 10_000 || string.IsNullOrWhiteSpace(recipient.Name)))
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Recipients require a name and a rate between 0.01% and 100%.");
        }

        var feeBps = schedule.BasisPointsFor(input.Method, input.Installments);
        var ibs = input.Gross.ApplyBasisPoints(ibsBps);
        var cbs = input.Gross.ApplyBasisPoints(cbsBps);
        var fee = input.Gross.ApplyBasisPoints(feeBps);
        var lines = new List<SplitLine>
        {
            new(SplitLineKind.TaxIbs, "Estados e Municípios (CG-IBS)", retained ? "IBS retido na venda (simulação)" : "IBS informativo 2026 (não recolhido)", ibsBps, ibs, retained),
            new(SplitLineKind.TaxCbs, "União (Receita Federal)", retained ? "CBS retida na venda (simulação)" : "CBS informativa 2026 (não recolhida)", cbsBps, cbs, retained),
            new(SplitLineKind.AcquirerFee, "Adquirente / subadquirente", "Taxa de desconto (MDR)", feeBps, fee, true)
        };

        var distributed = (retained ? ibs + cbs : Money.Zero) + fee;
        foreach (var recipient in recipients)
        {
            var amount = input.Gross.ApplyBasisPoints(recipient.BasisPoints);
            lines.Add(new SplitLine(SplitLineKind.Recipient, recipient.Name.Trim(), "Repasse a recebedor (split)", recipient.BasisPoints, amount, true));
            distributed += amount;
        }

        var net = input.Gross - distributed;
        if (net.Cents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Split lines exceed the gross amount.");
        }

        var netBps = (int)(10_000 - distributed.Cents * 10_000 / input.Gross.Cents);
        lines.Add(new SplitLine(SplitLineKind.Merchant, "Lojista", "Valor líquido do estabelecimento", netBps, net, true));

        return new SplitResult(
            input.Gross,
            feeBps,
            lines,
            net,
            fee,
            retained ? ibs + cbs : Money.Zero,
            retained ? Money.Zero : ibs + cbs,
            retained);
    }
}
