using System.Globalization;
using System.Text.Json;

namespace StranglerApp.NewOrders;

/// <summary>
/// REGRESSED VARIANT (demo/broken) — do not ship. Same shape as
/// OrdersSearchService, but the totals have drifted 5% above the legacy
/// handler: the generated shadow-diff suite must fail on this file,
/// proving the probes bite (SHADOW DRIFT on route 'orders.search').
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
        var total = (Prices.GetValueOrDefault(sku, 0m) * qty + 0.001m) * 1.05m;

        return "{\"sku\":\"" + JsonEncodedText.Encode(sku) + "\"," +
               "\"qty\":" + qty.ToString(CultureInfo.InvariantCulture) + "," +
               "\"lines\":[{\"sku\":\"" + JsonEncodedText.Encode(sku) + "\"," +
               "\"qty\":" + qty.ToString(CultureInfo.InvariantCulture) + "}]," +
               "\"total\":" + total.ToString("F3", CultureInfo.InvariantCulture) + "," +
               "\"servedBy\":\"new\",\"elapsedMs\":2}";
    }
}
