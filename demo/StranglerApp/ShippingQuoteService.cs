using System.Globalization;
using System.Text.Json;

namespace StranglerApp.NewOrders;

/// <summary>
/// The new shipping quote implementation that already owns the strangled
/// shipping.quote route. Quotes are reported at mill precision (F3); the
/// shadow diff keeps the retired legacy handler honest until deletion.
/// </summary>
public sealed class ShippingQuoteService
{
    public string Handle(string input)
    {
        using var request = JsonDocument.Parse(input);
        var weightKg = request.RootElement.GetProperty("weightKg").GetRawText();
        var destination = request.RootElement.GetProperty("destination").GetString() ?? string.Empty;
        var quote = 880m + request.RootElement.GetProperty("weightKg").GetDecimal() * 1.5m + 0.001m;

        return "{\"weightKg\":" + weightKg + "," +
               "\"destination\":\"" + JsonEncodedText.Encode(destination) + "\"," +
               "\"quote\":" + quote.ToString("F3", CultureInfo.InvariantCulture) + "," +
               "\"servedBy\":\"new\",\"elapsedMs\":2}";
    }
}
