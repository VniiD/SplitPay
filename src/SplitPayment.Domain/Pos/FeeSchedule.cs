namespace SplitPayment.Domain.Pos;

/// <summary>Fee and settlement rules. Values are illustrative demo defaults, not a real acquirer price list.</summary>
public sealed record FeeSchedule(
    int PixBasisPoints,
    int DebitBasisPoints,
    int CreditSingleBasisPoints,
    int CreditInstallments2To6BasisPoints,
    int CreditInstallments7To12BasisPoints,
    int VoucherBasisPoints,
    int MaxInstallments,
    long MinimumInstallmentCents)
{
    public static FeeSchedule Demo { get; } = new(99, 199, 349, 449, 499, 520, 12, 500);

    public int BasisPointsFor(PaymentMethod method, int installments) => method switch
    {
        PaymentMethod.Pix => PixBasisPoints,
        PaymentMethod.Debit => DebitBasisPoints,
        PaymentMethod.MealVoucher or PaymentMethod.FoodVoucher => VoucherBasisPoints,
        PaymentMethod.Credit when installments <= 1 => CreditSingleBasisPoints,
        PaymentMethod.Credit when installments <= 6 => CreditInstallments2To6BasisPoints,
        PaymentMethod.Credit => CreditInstallments7To12BasisPoints,
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    /// <summary>Days until the first settlement (D+n).</summary>
    public static int FirstSettlementDays(PaymentMethod method) => method switch
    {
        PaymentMethod.Pix => 0,
        PaymentMethod.Debit => 1,
        _ => 30
    };

    public void ValidateInstallments(PaymentMethod method, int installments, Money amount)
    {
        if (installments < 1 || installments > MaxInstallments)
        {
            throw new ArgumentOutOfRangeException(nameof(installments), $"Installments must be between 1 and {MaxInstallments}.");
        }

        if (installments > 1)
        {
            if (method != PaymentMethod.Credit)
            {
                throw new ArgumentOutOfRangeException(nameof(installments), "Only credit supports installments.");
            }

            if (amount.Cents / installments < MinimumInstallmentCents)
            {
                throw new ArgumentOutOfRangeException(nameof(installments), "Installment amount is below the minimum allowed.");
            }
        }
    }
}
