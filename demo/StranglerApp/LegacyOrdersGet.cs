using System.Text.Json;

namespace StranglerApp.LegacyWeb;

/// <summary>
/// The monolith's order lookup. Still a todo route: the strangler has not
/// started on orders.get, so there is no new implementation yet.
/// </summary>
public sealed class LegacyOrdersGet
{
    public string Handle(string input)
    {
        using var request = JsonDocument.Parse(input);
        var orderId = request.RootElement.GetProperty("orderId").GetString() ?? string.Empty;

        return "{\"orderId\":\"" + JsonEncodedText.Encode(orderId) + "\"," +
               "\"status\":\"shipped\"," +
               "\"servedBy\":\"legacy\",\"elapsedMs\":1}";
    }
}
