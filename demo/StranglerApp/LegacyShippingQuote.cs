using System.Globalization;
using System.Text.Json;

namespace StranglerApp.LegacyWeb;

/// <summary>
/// The monolith's shipping quote. Still compiled and shadowed, but the
/// route is already strangled: the pivot promotes shipping.quote to the
/// new implementation and the traffic has retired below the threshold.
/// </summary>
public sealed class LegacyShippingQuote
{
    public string Handle(string input)
    {
        using var request = JsonDocument.Parse(input);
        var weightKg = request.RootElement.GetProperty("weightKg").GetRawText();
        var destination = request.RootElement.GetProperty("destination").GetString() ?? string.Empty;
        var quote = 880m + request.RootElement.GetProperty("weightKg").GetDecimal() * 1.5m + 0.001m;

        return "{\"weightKg\":" + weightKg + "," +
               "\"destination\":\"" + JsonEncodedText.Encode(destination) + "\"," +
               "\"quote\":" + quote.ToString("F2", CultureInfo.InvariantCulture) + "," +
               "\"servedBy\":\"legacy\",\"elapsedMs\":4}";
    }
}
