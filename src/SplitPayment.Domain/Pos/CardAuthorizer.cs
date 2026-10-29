using System.Security.Cryptography;

namespace SplitPayment.Domain.Pos;

/// <summary>Deterministic issuer simulator. Never receives or stores a PAN; real terminals keep card data inside the certified PIN pad.</summary>
public static class CardAuthorizer
{
    private static readonly string[] SupportedBrands = ["VISA", "MASTERCARD", "ELO", "HIPERCARD", "AMEX", "ALELO", "SODEXO", "VR", "TICKET"];

    public static IReadOnlyList<string> Brands => SupportedBrands;

    public static CardInfo NormalizeCard(string? brand, string? last4, CardEntryMode entryMode, PaymentMethod method)
    {
        var normalizedBrand = (brand ?? string.Empty).Trim().ToUpperInvariant();
        if (!SupportedBrands.Contains(normalizedBrand))
        {
            throw new DomainRuleException("brand_invalid", "Unsupported card brand.");
        }

        if (last4 is not { Length: 4 } || !last4.All(char.IsAsciiDigit))
        {
            throw new DomainRuleException("last4_invalid", "Provide exactly the last four digits of the card.");
        }

        if (method is PaymentMethod.MealVoucher or PaymentMethod.FoodVoucher && entryMode == CardEntryMode.MagneticStripe)
        {
            entryMode = CardEntryMode.ChipAndPin;
        }

        var pinVerified = entryMode is CardEntryMode.ChipAndPin or CardEntryMode.MagneticStripe ||
            method is PaymentMethod.Debit or PaymentMethod.MealVoucher or PaymentMethod.FoodVoucher;
        return new CardInfo(normalizedBrand, last4, entryMode, pinVerified);
    }

    public static Authorization Authorize(SimulatedOutcome outcome, long nsuSequence)
    {
        var nsu = nsuSequence.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
        return outcome switch
        {
            SimulatedOutcome.Approve => new Authorization(true, "00", "Transação aprovada", NewAuthorizationCode(), nsu),
            SimulatedOutcome.InsufficientFunds => new Authorization(false, "51", "Saldo ou limite insuficiente", null, nsu),
            SimulatedOutcome.ExpiredCard => new Authorization(false, "54", "Cartão vencido", null, nsu),
            _ => new Authorization(false, "05", "Transação não autorizada pelo emissor", null, nsu)
        };
    }

    private static string NewAuthorizationCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(6, alphabet, static (span, chars) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }
}
