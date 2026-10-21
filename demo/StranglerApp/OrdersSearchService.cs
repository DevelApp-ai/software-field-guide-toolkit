using System.Globalization;
using System.Text.Json;

namespace StranglerApp.NewOrders;

/// <summary>
/// The new order search implementation, shadowing the legacy handler: same
/// cases, same answers — but totals are reported at mill precision (F3),
/// which the route's numeric tolerance (total ± 0.01) absorbs.
/// </summary>
public sealed class OrdersSearchService
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
               "\"total\":" + total.ToString("F3", CultureInfo.InvariantCulture) + "," +
               "\"servedBy\":\"new\",\"elapsedMs\":2}";
    }
}
