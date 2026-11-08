using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AppDistribuidaAspire.ApiService.Payments;

public static class MercadoPagoWebhookSignatureValidator
{
    public static bool IsValid(string? signatureHeader, string? requestId, string? paymentId, string? secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) ||
            string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(paymentId) ||
            string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !values.TryAdd(pair[0], pair[1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("ts", out var timestamp) ||
            !values.TryGetValue("v1", out var providedSignature) ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !long.TryParse(paymentId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericPaymentId) ||
            numericPaymentId <= 0 ||
            !TryDecodeHex(providedSignature, out var providedHash))
        {
            return false;
        }

        var manifest = $"id:{paymentId.ToLowerInvariant()};request-id:{requestId};ts:{timestamp};";
        var expectedHash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        return providedHash.Length == expectedHash.Length && CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
