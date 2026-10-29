namespace SplitPayment.Domain.Pos;

public sealed record Receivable(
    int Number,
    int Total,
    DateOnly DueDate,
    Money Gross,
    Money Fee,
    Money Net)
{
    public Money RefundedGross { get; init; } = Money.Zero;
}

public static class ReceivableScheduler
{
    /// <summary>Builds the settlement schedule: gross and fee are allocated cent-exact across installments.</summary>
    public static IReadOnlyList<Receivable> Build(
        PaymentMethod method,
        int installments,
        Money gross,
        Money totalFee,
        DateOnly saleDate)
    {
        var grossParts = gross.AllocateEvenly(installments);
        var feeParts = totalFee.Allocate(grossParts.Select(part => part.Cents).ToArray());
        var firstDays = FeeSchedule.FirstSettlementDays(method);
        var receivables = new List<Receivable>(installments);
        for (var index = 0; index < installments; index++)
        {
            var due = method == PaymentMethod.Credit || method is PaymentMethod.MealVoucher or PaymentMethod.FoodVoucher
                ? saleDate.AddDays(30 * (index + 1))
                : saleDate.AddDays(firstDays);
            receivables.Add(new Receivable(index + 1, installments, due, grossParts[index], feeParts[index], grossParts[index] - feeParts[index]));
        }

        return receivables;
    }
}
