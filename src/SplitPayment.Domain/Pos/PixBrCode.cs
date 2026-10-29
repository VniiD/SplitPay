using System.Globalization;
using System.Text;

namespace SplitPayment.Domain.Pos;

/// <summary>Generates a Pix "copia e cola" EMV payload (BR Code) with CRC16-CCITT.</summary>
public static class PixBrCode
{
    public static string Build(string pixKey, string merchantName, string city, Money amount, string transactionId)
    {
        if (string.IsNullOrWhiteSpace(pixKey) || pixKey.Length > 77)
        {
            throw new ArgumentException("Invalid Pix key.", nameof(pixKey));
        }

        if (amount.Cents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var txid = new string(transactionId.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (txid.Length is < 1 or > 25)
        {
            txid = txid.Length == 0 ? "***" : txid[..25];
        }

        var account = Tlv("00", "br.gov.bcb.pix") + Tlv("01", pixKey);
        var payload = new StringBuilder()
            .Append(Tlv("00", "01"))
            .Append(Tlv("01", "12"))
            .Append(Tlv("26", account))
            .Append(Tlv("52", "0000"))
            .Append(Tlv("53", "986"))
            .Append(Tlv("54", (amount.Cents / 100m).ToString("0.00", CultureInfo.InvariantCulture)))
            .Append(Tlv("58", "BR"))
            .Append(Tlv("59", Sanitize(merchantName, 25)))
            .Append(Tlv("60", Sanitize(city, 15)))
            .Append(Tlv("62", Tlv("05", txid)))
            .Append("6304")
            .ToString();

        return payload + Crc16(payload).ToString("X4", CultureInfo.InvariantCulture);
    }

    public static bool IsValid(string payload)
    {
        if (payload.Length < 8 || !payload.Contains("6304", StringComparison.Ordinal))
        {
            return false;
        }

        var body = payload[..^4];
        return body.EndsWith("6304", StringComparison.Ordinal) &&
            string.Equals(Crc16(body).ToString("X4", CultureInfo.InvariantCulture), payload[^4..], StringComparison.Ordinal);
    }

    public static ushort Crc16(string value)
    {
        ushort crc = 0xFFFF;
        foreach (var data in Encoding.UTF8.GetBytes(value))
        {
            crc ^= (ushort)(data << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    private static string Tlv(string id, string value) => id + value.Length.ToString("00", CultureInfo.InvariantCulture) + value;

    private static string Sanitize(string value, int maxLength)
    {
        var normalized = value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == ' ')
            .ToArray();
        var text = new string(normalized).Trim().ToUpperInvariant();
        return text.Length == 0 ? "NA" : text.Length > maxLength ? text[..maxLength] : text;
    }
}
