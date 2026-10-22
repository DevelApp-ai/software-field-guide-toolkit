using System.Text.Json;

namespace Sfg.Toolkit.SeamLedger;

public sealed record SeamEvidence(
    string VariationAxis,
    IReadOnlyList<string> Teams,
    string DesignDoc)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(VariationAxis) &&
        Teams.Count >= 2 &&
        !string.IsNullOrWhiteSpace(DesignDoc);
}

public static class EvidenceStore
{
    public static IReadOnlyDictionary<string, SeamEvidence> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new Dictionary<string, SeamEvidence>();
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, SeamEvidence>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            string? axis = null;
            var teams = new List<string>();
            string? doc = null;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (property.Value.TryGetProperty("variationAxis", out var axisElement) &&
                    axisElement.ValueKind == JsonValueKind.String)
                {
                    axis = axisElement.GetString();
                }

                if (property.Value.TryGetProperty("teams", out var teamsElement) &&
                    teamsElement.ValueKind == JsonValueKind.Array)
                {
                    teams.AddRange(teamsElement
                        .EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString() ?? string.Empty)
                        .Where(t => t.Length > 0));
                }

                if (property.Value.TryGetProperty("designDoc", out var docElement) &&
                    docElement.ValueKind == JsonValueKind.String)
                {
                    doc = docElement.GetString();
                }
            }

            result[property.Name] = new SeamEvidence(
                axis ?? string.Empty,
                teams,
                doc ?? string.Empty);
        }

        return result;
    }
}
