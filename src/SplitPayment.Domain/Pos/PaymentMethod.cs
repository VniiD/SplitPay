namespace SplitPayment.Domain.Pos;

public enum PaymentMethod
{
    Pix = 1,
    Debit = 2,
    Credit = 3,
    MealVoucher = 4,
    FoodVoucher = 5
}

public enum CardEntryMode
{
    ChipAndPin = 1,
    Contactless = 2,
    MagneticStripe = 3,
    Manual = 4
}

public enum SimulatedOutcome
{
    Approve = 0,
    InsufficientFunds = 1,
    DoNotHonor = 2,
    ExpiredCard = 3
}

public static class PaymentMethodExtensions
{
    public static string Label(this PaymentMethod method, int installments) => method switch
    {
        PaymentMethod.Pix => "Pix",
        PaymentMethod.Debit => "Cartão de débito",
        PaymentMethod.Credit => installments > 1 ? $"Cartão de crédito parcelado em {installments}x" : "Cartão de crédito à vista",
        PaymentMethod.MealVoucher => "Vale-refeição (VR)",
        PaymentMethod.FoodVoucher => "Vale-alimentação (VA)",
        _ => method.ToString()
    };

    public static bool IsCard(this PaymentMethod method) => method != PaymentMethod.Pix;
}
