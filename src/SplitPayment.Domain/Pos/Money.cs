using System.Globalization;

namespace SplitPayment.Domain.Pos;

/// <summary>Monetary amount in BRL stored as integer cents to avoid rounding drift.</summary>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    public static Money Zero => new(0);

    public static Money FromCents(long cents) => new(cents);

    public static Money Add(Money left, Money right) => new(checked(left.Cents + right.Cents));

    public static Money operator +(Money left, Money right) => Add(left, right);

    public static Money operator -(Money left, Money right) => new(checked(left.Cents - right.Cents));

    public static bool operator <(Money left, Money right) => left.Cents < right.Cents;

    public static bool operator >(Money left, Money right) => left.Cents > right.Cents;

    public static bool operator <=(Money left, Money right) => left.Cents <= right.Cents;

    public static bool operator >=(Money left, Money right) => left.Cents >= right.Cents;

    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

    /// <summary>Applies a rate expressed in basis points (1 bp = 0.01%) rounding half away from zero.</summary>
    public Money ApplyBasisPoints(int basisPoints)
    {
        var scaled = checked((decimal)Cents * basisPoints / 10_000m);
        return new Money((long)decimal.Round(scaled, 0, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Splits the amount proportionally to the weights using the largest remainder method,
    /// guaranteeing that the parts always add up to the original amount.
    /// </summary>
    public Money[] Allocate(IReadOnlyList<long> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0 || weights.Any(weight => weight < 0))
        {
            throw new ArgumentException("Weights must be non-empty and non-negative.", nameof(weights));
        }

        var total = weights.Sum();
        if (total == 0)
        {
            throw new ArgumentException("At least one weight must be positive.", nameof(weights));
        }

        var sign = Cents < 0 ? -1 : 1;
        var absolute = Math.Abs(Cents);
        var shares = new long[weights.Count];
        var remainders = new (int Index, decimal Remainder)[weights.Count];
        long allocated = 0;
        for (var index = 0; index < weights.Count; index++)
        {
            var exact = (decimal)absolute * weights[index] / total;
            shares[index] = (long)decimal.Floor(exact);
            remainders[index] = (index, exact - shares[index]);
            allocated += shares[index];
        }

        var leftover = absolute - allocated;
        foreach (var (index, _) in remainders.OrderByDescending(item => item.Remainder).ThenBy(item => item.Index).Take((int)leftover))
        {
            shares[index]++;
        }

        return shares.Select(share => new Money(share * sign)).ToArray();
    }

    public Money[] AllocateEvenly(int parts) => Allocate(Enumerable.Repeat(1L, parts).ToArray());

    public override string ToString()
    {
        var absolute = Math.Abs(Cents);
        var text = (absolute / 100m).ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
        return $"{(Cents < 0 ? "-" : string.Empty)}R$ {text}";
    }
}
