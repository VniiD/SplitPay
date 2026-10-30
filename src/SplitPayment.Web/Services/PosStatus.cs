namespace SplitPayment.Web.Services;

public static class PosStatus
{
    public static string Label(string status) => status switch
    {
        "PendingPayment" => "Aguardando pagamento",
        "Captured" => "Aprovada",
        "Declined" => "Negada",
        "Expired" => "Expirada",
        "Cancelled" => "Cancelada",
        "PartiallyRefunded" => "Estorno parcial",
        "Refunded" => "Estornada",
        _ => status
    };
}
