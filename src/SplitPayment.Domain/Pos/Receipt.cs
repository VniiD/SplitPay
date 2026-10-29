using System.Globalization;
using System.Text;

namespace SplitPayment.Domain.Pos;

public enum ReceiptVia
{
    Customer = 1,
    Merchant = 2
}

public sealed record ReceiptField(string Label, string Value);

public sealed record Receipt(
    ReceiptVia Via,
    string Title,
    string Status,
    IReadOnlyList<ReceiptField> Merchant,
    IReadOnlyList<ReceiptField> Transaction,
    IReadOnlyList<ReceiptField> Payment,
    IReadOnlyList<ReceiptField> Installments,
    IReadOnlyList<ReceiptField> Split,
    IReadOnlyList<ReceiptField> Refunds,
    IReadOnlyList<string> Notices,
    string Footer);

/// <summary>
/// Builds the payment receipt (comprovante) in the usual Brazilian POS layout: merchant identification, terminal,
/// NSU / authorization code, masked card, installments and the customer / merchant copies.
/// It is a simulation and never has fiscal value.
/// </summary>
public static class ReceiptBuilder
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static Receipt Build(Sale sale, Merchant merchant, Terminal terminal, ReceiptVia via, TimeSpan utcOffset)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(merchant);
        ArgumentNullException.ThrowIfNull(terminal);

        var local = sale.CreatedAt.ToOffset(utcOffset);
        var completed = sale.CapturedAt?.ToOffset(utcOffset) ?? local;
        var title = sale.Status switch
        {
            SaleStatus.Declined => "TRANSAÇÃO NEGADA",
            SaleStatus.Expired => "COBRANÇA PIX EXPIRADA",
            SaleStatus.PendingPayment => "AGUARDANDO PAGAMENTO",
            SaleStatus.Cancelled => "VENDA CANCELADA",
            SaleStatus.Refunded => "VENDA ESTORNADA",
            _ => "COMPROVANTE DE PAGAMENTO"
        };

        var merchantFields = new List<ReceiptField>
        {
            new("Estabelecimento", merchant.TradeName),
            new("Razão social", merchant.LegalName),
            new("CNPJ", FormatCnpj(merchant.TaxId)),
            new("Endereço", $"{merchant.AddressLine}, {merchant.City}/{merchant.State}"),
            new("MCC", merchant.Mcc),
            new("Terminal", $"{terminal.Id} ({terminal.SerialNumber})")
        };

        var transaction = new List<ReceiptField>
        {
            new("Data/hora", completed.ToString("dd/MM/yyyy HH:mm:ss", Culture)),
            new("Venda", sale.Id.ToString("D")),
            new("Sequencial", sale.Sequence.ToString("000000", Culture)),
            new("Situação", StatusLabel(sale.Status))
        };

        var payment = new List<ReceiptField>
        {
            new("Operação", sale.Method.Label(sale.Installments)),
            new("Valor", sale.Gross.ToString())
        };

        if (sale.Card is { } card)
        {
            payment.Add(new("Bandeira", card.Brand));
            payment.Add(new("Cartão", $"**** **** **** {card.Last4}"));
            payment.Add(new("Captura", EntryModeLabel(card.EntryMode)));
            payment.Add(new("Verificação", card.PinVerified ? "Senha verificada" : "Aprovada sem senha"));
        }

        if (sale.Authorization is { } authorization)
        {
            payment.Add(new("NSU", authorization.Nsu));
            payment.Add(new("Autorização", authorization.AuthorizationCode ?? "-"));
            payment.Add(new("Resposta", $"{authorization.ResponseCode} - {authorization.Message}"));
        }

        if (sale.Pix is { } pix)
        {
            payment.Add(new("Chave Pix", Mask(merchant.PixKey)));
            payment.Add(new("TxId", pix.TransactionId));
            if (pix.EndToEndId is not null)
            {
                payment.Add(new("EndToEndId", pix.EndToEndId));
                payment.Add(new("Pagador", pix.PayerName ?? "-"));
            }
        }

        var installmentFields = sale.Installments > 1
            ? sale.Receivables.Select(item => new ReceiptField(
                $"{item.Number}/{item.Total}",
                $"{item.Gross} em {item.DueDate.ToString("dd/MM/yyyy", Culture)}")).ToList()
            : [];

        var split = via == ReceiptVia.Merchant ? SplitFields(sale) : TaxFieldsOnly(sale);
        var refunds = sale.Refunds
            .Select(refund => new ReceiptField(refund.At.ToOffset(utcOffset).ToString("dd/MM/yyyy HH:mm", Culture), $"{refund.Amount} - {refund.Reason}"))
            .ToList();

        var notices = new List<string>();
        if (sale.Status == SaleStatus.Declined)
        {
            notices.Add("Transação não efetuada. Nenhum valor foi cobrado.");
        }
        else if (sale.Status is SaleStatus.Captured or SaleStatus.PartiallyRefunded)
        {
            notices.Add(sale.Card is { PinVerified: true } ? "Transação autorizada mediante senha." : "Transação autorizada.");
        }

        notices.Add(via == ReceiptVia.Customer ? "VIA CLIENTE" : "VIA ESTABELECIMENTO");
        notices.Add("SIMULAÇÃO PARA DEMONSTRAÇÃO - SEM VALOR FISCAL. Não substitui NFC-e/NF-e.");

        return new Receipt(
            via,
            title,
            sale.Status.ToString(),
            merchantFields,
            transaction,
            payment,
            installmentFields,
            split,
            refunds,
            notices,
            "Guarde este comprovante. Dúvidas: SAC do estabelecimento.");
    }

    public static string RenderText(Receipt receipt, int width = 40)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var builder = new StringBuilder();
        var rule = new string('-', width);
        builder.AppendLine(Center(receipt.Title, width));
        builder.AppendLine(rule);
        Section(builder, null, receipt.Merchant, width);
        Section(builder, null, receipt.Transaction, width);
        Section(builder, "PAGAMENTO", receipt.Payment, width);
        Section(builder, "PARCELAS", receipt.Installments, width);
        Section(builder, receipt.Via == ReceiptVia.Merchant ? "SPLIT / REPASSE" : "TRIBUTOS (INFORMATIVO)", receipt.Split, width);
        Section(builder, "ESTORNOS", receipt.Refunds, width);
        foreach (var notice in receipt.Notices)
        {
            AppendCentered(builder, notice, width);
        }

        builder.AppendLine(rule);
        AppendCentered(builder, receipt.Footer, width);
        return builder.ToString();
    }

    private static List<ReceiptField> SplitFields(Sale sale)
    {
        var fields = sale.Split.Lines
            .Select(line => new ReceiptField(
                $"{line.Beneficiary}{(line.Retained ? string.Empty : " *")}",
                $"{line.Amount} ({(line.BasisPoints / 100m).ToString("0.00", Culture)}%)"))
            .ToList();
        if (!sale.Split.TaxRetainedAtSale)
        {
            fields.Add(new("*", "Informativo (fase de teste 2026), não retido"));
        }

        return fields;
    }

    private static List<ReceiptField> TaxFieldsOnly(Sale sale) => sale.Split.Lines
        .Where(line => line.Kind is SplitLineKind.TaxIbs or SplitLineKind.TaxCbs)
        .Select(line => new ReceiptField(
            line.Kind == SplitLineKind.TaxIbs ? "IBS" : "CBS",
            $"{line.Amount}{(line.Retained ? string.Empty : " (informativo)")}"))
        .ToList();

    private static void Section(StringBuilder builder, string? heading, IReadOnlyList<ReceiptField> fields, int width)
    {
        if (fields.Count == 0)
        {
            return;
        }

        if (heading is not null)
        {
            builder.AppendLine(Center($"[ {heading} ]", width));
        }

        foreach (var field in fields)
        {
            var line = $"{field.Label}: {field.Value}";
            foreach (var chunk in Wrap(line, width))
            {
                builder.AppendLine(chunk);
            }
        }

        builder.AppendLine(new string('-', width));
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        for (var index = 0; index < text.Length; index += width)
        {
            yield return text.Substring(index, Math.Min(width, text.Length - index));
        }
    }

    private static void AppendCentered(StringBuilder builder, string text, int width)
    {
        foreach (var words in WrapWords(text, width))
        {
            builder.AppendLine(Center(words, width));
        }
    }

    private static IEnumerable<string> WrapWords(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : string.Empty).Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string Center(string text, int width) =>
        text.Length >= width ? text : new string(' ', (width - text.Length) / 2) + text;

    private static string Mask(string value) => value.Length <= 8 ? "****" : $"{value[..4]}…{value[^4..]}";

    private static string FormatCnpj(string value) => value.Length == 14
        ? $"{value[..2]}.{value[2..5]}.{value[5..8]}/{value[8..12]}-{value[12..]}"
        : value;

    private static string EntryModeLabel(CardEntryMode mode) => mode switch
    {
        CardEntryMode.ChipAndPin => "Chip",
        CardEntryMode.Contactless => "Aproximação (NFC)",
        CardEntryMode.MagneticStripe => "Tarja magnética",
        _ => "Digitado"
    };

    private static string StatusLabel(SaleStatus status) => status switch
    {
        SaleStatus.PendingPayment => "Aguardando pagamento",
        SaleStatus.Captured => "Aprovada",
        SaleStatus.Declined => "Negada",
        SaleStatus.Expired => "Expirada",
        SaleStatus.Cancelled => "Cancelada",
        SaleStatus.PartiallyRefunded => "Parcialmente estornada",
        SaleStatus.Refunded => "Estornada",
        _ => status.ToString()
    };
}
