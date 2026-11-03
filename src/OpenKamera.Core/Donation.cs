using System.Text.Json;

namespace OpenKamera.Core;

/// <summary>
/// Where donations go. Read from <c>donate.json</c> next to the app so forks can point it at their own
/// accounts without touching code. Empty fields hide that method.
/// </summary>
public sealed class DonationInfo
{
    public string PayPalUrl { get; set; } = "";
    public string KoFiUrl { get; set; } = "";
    public string UpiId { get; set; } = "";
    public string UpiName { get; set; } = "";
    public string Message { get; set; } = "";

    public bool HasPayPal => Uri.TryCreate(PayPalUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
    public bool HasKoFi => Uri.TryCreate(KoFiUrl, UriKind.Absolute, out var k) && k.Scheme == Uri.UriSchemeHttps;
    public bool HasUpi => IsValidUpiId(UpiId);
    public bool IsConfigured => HasPayPal || HasKoFi || HasUpi;

    /// <summary>UPI deep link any UPI app understands (GPay, PhonePe, Paytm, BHIM). Amount left to the donor.</summary>
    public string UpiUri =>
        // pa stays literal (handle@psp): several UPI apps reject a percent-encoded '@'. IsValidUpiId guarantees it is URL-safe.
        $"upi://pay?pa={UpiId}&pn={Uri.EscapeDataString(UpiName)}&cu=INR&tn={Uri.EscapeDataString("KISSIK donation")}";

    /// <summary>handle@psp — letters, digits, dot, dash, underscore; PSP letters only.</summary>
    public static bool IsValidUpiId(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id ?? "", @"^[A-Za-z0-9._-]{2,256}@[A-Za-z]{2,64}$");

    /// <summary>PNG bytes of a QR code for the UPI link (dark modules on white, quiet zone included).</summary>
    public byte[] UpiQrPng(int pixelsPerModule = 8)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(UpiUri, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public static DonationInfo Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<DonationInfo>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { /* treat as unconfigured */ }
        return new();
    }
}
