namespace SplitPayment.Domain.Pos;

public enum SaleStatus
{
    PendingPayment = 1,
    Captured = 2,
    Declined = 3,
    Expired = 4,
    Cancelled = 5,
    PartiallyRefunded = 6,
    Refunded = 7
}

public sealed record CardInfo(string Brand, string Last4, CardEntryMode EntryMode, bool PinVerified);

public sealed record Authorization(
    bool Approved,
    string ResponseCode,
    string Message,
    string? AuthorizationCode,
    string Nsu);

public sealed record PixInfo(string TransactionId, string CopyAndPaste, DateTimeOffset ExpiresAt, string? EndToEndId, string? PayerName);

public sealed record RefundRecord(Guid Id, Money Amount, DateTimeOffset At, string Reason);

public sealed record AuditEntry(DateTimeOffset At, string Event, string Detail);

public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class Sale
{
    private readonly List<RefundRecord> _refunds = [];
    private readonly List<AuditEntry> _audit = [];

    public Sale(
        Guid id,
        Guid merchantId,
        string terminalId,
        PaymentMethod method,
        int installments,
        Money gross,
        SplitResult split,
        IReadOnlyList<Receivable> receivables,
        DateTimeOffset createdAt,
        long sequence)
    {
        Id = id;
        MerchantId = merchantId;
        TerminalId = terminalId;
        Method = method;
        Installments = installments;
        Gross = gross;
        Split = split;
        Receivables = receivables;
        CreatedAt = createdAt;
        Sequence = sequence;
        Status = SaleStatus.PendingPayment;
        Record(createdAt, "sale.created", $"{method.Label(installments)} {gross}");
    }

    public Guid Id { get; }
    public Guid MerchantId { get; }
    public string TerminalId { get; }
    public PaymentMethod Method { get; }
    public int Installments { get; }
    public Money Gross { get; }
    public SplitResult Split { get; }
    public IReadOnlyList<Receivable> Receivables { get; }
    public DateTimeOffset CreatedAt { get; }
    public long Sequence { get; }
    public SaleStatus Status { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public CardInfo? Card { get; private set; }
    public Authorization? Authorization { get; private set; }
    public PixInfo? Pix { get; private set; }
    public string? DeclineReason { get; private set; }
    public IReadOnlyList<RefundRecord> Refunds => _refunds;
    public IReadOnlyList<AuditEntry> Audit => _audit;
    public Money RefundedTotal => _refunds.Aggregate(Money.Zero, (sum, refund) => sum + refund.Amount);
    public Money RefundableBalance => Status is SaleStatus.Captured or SaleStatus.PartiallyRefunded ? Gross - RefundedTotal : Money.Zero;

    public void AttachPix(PixInfo pix)
    {
        EnsureStatus("pix_invalid_state", SaleStatus.PendingPayment);
        if (Method != PaymentMethod.Pix)
        {
            throw new DomainRuleException("not_pix", "Sale is not a Pix sale.");
        }

        Pix = pix;
        Record(CreatedAt, "pix.qr_generated", pix.TransactionId);
    }

    public void ApplyCardAuthorization(CardInfo card, Authorization authorization, DateTimeOffset now)
    {
        EnsureStatus("authorization_invalid_state", SaleStatus.PendingPayment);
        if (!Method.IsCard())
        {
            throw new DomainRuleException("not_card", "Sale is not a card sale.");
        }

        Card = card;
        Authorization = authorization;
        if (authorization.Approved)
        {
            Status = SaleStatus.Captured;
            CapturedAt = now;
            Record(now, "card.authorized", $"NSU {authorization.Nsu} AUT {authorization.AuthorizationCode}");
        }
        else
        {
            Status = SaleStatus.Declined;
            DeclineReason = authorization.Message;
            Record(now, "card.declined", $"{authorization.ResponseCode} {authorization.Message}");
        }
    }

    public void ConfirmPix(string endToEndId, string payerName, DateTimeOffset now)
    {
        EnsureStatus("pix_invalid_state", SaleStatus.PendingPayment);
        if (Pix is null)
        {
            throw new DomainRuleException("pix_missing", "Pix charge was not generated.");
        }

        if (now > Pix.ExpiresAt)
        {
            Status = SaleStatus.Expired;
            Record(now, "pix.expired", Pix.TransactionId);
            throw new DomainRuleException("pix_expired", "The Pix charge has expired.");
        }

        Pix = Pix with { EndToEndId = endToEndId, PayerName = payerName };
        Status = SaleStatus.Captured;
        CapturedAt = now;
        Record(now, "pix.confirmed", endToEndId);
    }

    public bool ExpirePixIfDue(DateTimeOffset now)
    {
        if (Status != SaleStatus.PendingPayment || Pix is null || now <= Pix.ExpiresAt)
        {
            return false;
        }

        Status = SaleStatus.Expired;
        Record(now, "pix.expired", Pix.TransactionId);
        return true;
    }

    /// <summary>Same-day void of a captured card sale, before any refund.</summary>
    public void Cancel(DateTimeOffset now, string reason, TimeSpan offset)
    {
        EnsureStatus("cancel_invalid_state", SaleStatus.Captured);
        if (Method == PaymentMethod.Pix)
        {
            throw new DomainRuleException("pix_cannot_be_cancelled", "Pix payments must be returned through a refund (devolução).");
        }

        if (_refunds.Count > 0 || DateOnly.FromDateTime(CapturedAt!.Value.ToOffset(offset).DateTime) != DateOnly.FromDateTime(now.ToOffset(offset).DateTime))
        {
            throw new DomainRuleException("cancel_window_closed", "Cancellation is only allowed on the capture day; use a refund.");
        }

        Status = SaleStatus.Cancelled;
        Record(now, "sale.cancelled", reason);
    }

    public RefundRecord Refund(Money amount, string reason, DateTimeOffset now)
    {
        EnsureStatus("refund_invalid_state", SaleStatus.Captured, SaleStatus.PartiallyRefunded);
        if (amount.Cents <= 0 || amount > RefundableBalance)
        {
            throw new DomainRuleException("refund_amount_invalid", "Refund amount must be positive and not exceed the refundable balance.");
        }

        var refund = new RefundRecord(Guid.NewGuid(), amount, now, reason);
        _refunds.Add(refund);
        Status = RefundedTotal == Gross ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded;
        Record(now, "sale.refunded", $"{amount} - {reason}");
        return refund;
    }

    /// <summary>Net amount still owed to the merchant after refunds (refunds reduce the net proportionally).</summary>
    public Money MerchantNetAfterRefunds
    {
        get
        {
            if (Status is SaleStatus.Declined or SaleStatus.Expired or SaleStatus.Cancelled or SaleStatus.PendingPayment)
            {
                return Money.Zero;
            }

            var kept = Gross - RefundedTotal;
            return kept.Cents == 0 ? Money.Zero : new Money(Split.MerchantNet.Cents * kept.Cents / Gross.Cents);
        }
    }

    public bool CountsAsRevenue => Status is SaleStatus.Captured or SaleStatus.PartiallyRefunded or SaleStatus.Refunded;

    public void NoteLedgerDeferred(DateTimeOffset now) => Record(now, "ledger.deferred", "Ledger publication pending retry");

    private void EnsureStatus(string code, params SaleStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new DomainRuleException(code, $"Operation not allowed while the sale is {Status}.");
        }
    }

    private void Record(DateTimeOffset at, string name, string detail) => _audit.Add(new AuditEntry(at, name, detail));
}
