using System.Globalization;
using System.Text.Json;

namespace StranglerApp.LegacyWeb;

/// <summary>
/// The monolith's order search. Still serving orders.search while the new
/// implementation shadows it. Totals are rounded to cents (F2), which
/// absorbs the platform fee the new implementation reports at mills.
/// </summary>
public sealed class LegacyOrdersSearch
{
    private static readonly Dictionary<string, decimal> Prices = new(StringComparer.Ordinal)
    {
        ["A-1001"] = 50m,
        ["B-2002"] = 12.5m,
    };

    public string Handle(string input)
    {
        using var request = JsonDocument.Parse(input);
        var sku = request.RootElement.GetProperty("sku").GetString() ?? string.Empty;
        var qty = request.RootElement.GetProperty("qty").GetInt32();
        var total = Prices.GetValueOrDefault(sku, 0m) * qty + 0.001m;

        return "{\"sku\":\"" + JsonEncodedText.Encode(sku) + "\"," +
               "\"qty\":" + qty.ToString(CultureInfo.InvariantCulture) + "," +
               "\"lines\":[{\"sku\":\"" + JsonEncodedText.Encode(sku) + "\"," +
               "\"qty\":" + qty.ToString(CultureInfo.InvariantCulture) + "}]," +
               "\"total\":" + total.ToString("F2", CultureInfo.InvariantCulture) + "," +
               "\"servedBy\":\"legacy\",\"elapsedMs\":3}";
    }
}
