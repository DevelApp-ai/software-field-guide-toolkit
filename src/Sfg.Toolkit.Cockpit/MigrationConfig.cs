using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sfg.Toolkit.Cockpit;

/// <summary>
/// Per-route tolerance rules for the shadow comparison: numeric fields may
/// drift within a stated absolute tolerance (keyed by field name, at any
/// depth); ignored fields are excluded from the comparison entirely.
/// </summary>
public sealed record ShadowTolerance(
    IReadOnlyDictionary<string, decimal> Numeric,
    IReadOnlyList<string> Ignore);

/// <summary>
/// One declared route of the strangler migration: id, state
/// (todo / strangling / strangled), handler bindings, shadow-diff settings,
/// per-period old-implementation traffic, and retirement thresholds.
/// <see cref="Line"/> cites the route's "id" property in the route table.
/// </summary>
public sealed record MigrationRoute(
    string Id,
    string State,
    string? Old,
    string? New,
    string Method,
    bool Shadow,
    string? Cases,
    ShadowTolerance? Tolerance,
    IReadOnlyList<long> Traffic,
    decimal? RetirementThreshold,
    int? SunsetPeriods,
    int Line);

/// <summary>
/// The declared route table (migration.json) — the strangler migration's
/// single source of truth. Parsed with loud errors: unknown states,
/// duplicate ids, and shadow routes missing their bindings never pass
/// silently. Never a guess.
/// </summary>
public sealed class MigrationConfig
{
    public string? Project { get; init; }

    public string RouteMethod { get; init; } = "MapRoute";

    public decimal DefaultRetirementThreshold { get; init; } = 100;

    public int DefaultSunsetPeriods { get; init; } = 3;

    public IReadOnlyList<MigrationRoute> Routes { get; init; } = Array.Empty<MigrationRoute>();

    public static MigrationConfig Default { get; } = new();

    public static MigrationConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"route table not found: {path ?? "<null>"} — pass the migration route table with --config migration.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"route table is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("route table must be a JSON object with a \"routes\" array");
            }

            if (!root.TryGetProperty("routes", out var routesElement) ||
                routesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("route table must contain a \"routes\" array");
            }

            var routes = new List<MigrationRoute>();
            foreach (var entry in routesElement.EnumerateArray())
            {
                routes.Add(ReadRoute(entry, text));
            }

            foreach (var group in routes.GroupBy(r => r.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"route table declares '{group.Key}' {group.Count()} times — a route id must be unique");
                }
            }

            var defaultSunsetPeriods = (int?)ReadNumber(root, "defaultSunsetPeriods") ?? 3;
            if (defaultSunsetPeriods < 1)
            {
                throw new InvalidOperationException("defaultSunsetPeriods must be at least 1");
            }

            return new MigrationConfig
            {
                Project = ReadString(root, "project"),
                RouteMethod = ReadString(root, "routeMethod") ?? "MapRoute",
                DefaultRetirementThreshold = ReadNumber(root, "defaultRetirementThreshold") ?? 100,
                DefaultSunsetPeriods = defaultSunsetPeriods,
                Routes = routes.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
            };
        }
    }

    private static MigrationRoute ReadRoute(JsonElement entry, string tableText)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("every route entry must be a JSON object");
        }

        var id = ReadString(entry, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("every route entry needs a non-empty \"id\"");
        }

        var state = ReadString(entry, "state");
        if (state is not ("todo" or "strangling" or "strangled"))
        {
            throw new InvalidOperationException(
                $"route '{id}': unknown state '{state}' — expected todo, strangling, or strangled");
        }

        var old = ReadString(entry, "old");
        var @new = ReadString(entry, "new");
        if ((state == "strangling" || state == "strangled") && (old is null || @new is null))
        {
            throw new InvalidOperationException(
                $"route '{id}': a {state} route must name both \"old\" and \"new\" handlers");
        }

        var shadow = ReadBool(entry, "shadow");
        var cases = ReadString(entry, "cases");
        if (shadow && (old is null || @new is null || cases is null))
        {
            throw new InvalidOperationException(
                $"route '{id}': a shadow-diff route must name \"old\", \"new\", and \"cases\"");
        }

        var traffic = ReadTraffic(entry, id);
        var threshold = ReadNumber(entry, "retirementThreshold");
        var periods = (int?)ReadNumber(entry, "sunsetPeriods");
        if (periods is < 1)
        {
            throw new InvalidOperationException($"route '{id}': sunsetPeriods must be at least 1");
        }

        return new MigrationRoute(
            id,
            state,
            old,
            @new,
            ReadString(entry, "method") ?? "Handle",
            shadow,
            cases,
            ReadTolerance(entry),
            traffic,
            threshold,
            periods,
            LineOfId(tableText, id));
    }

    private static IReadOnlyList<long> ReadTraffic(JsonElement entry, string id)
    {
        if (!entry.TryGetProperty("traffic", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<long>();
        }

        var traffic = new List<long>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
            {
                throw new InvalidOperationException(
                    $"route '{id}': traffic entries must be non-negative integers");
            }

            traffic.Add(count);
        }

        return traffic;
    }

    private static ShadowTolerance? ReadTolerance(JsonElement entry)
    {
        if (!entry.TryGetProperty("tolerance", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var numeric = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (element.TryGetProperty("numeric", out var numericElement) &&
            numericElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in numericElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    numeric[property.Name] = property.Value.GetDecimal();
                }
            }
        }

        var ignore = new List<string>();
        if (element.TryGetProperty("ignore", out var ignoreElement) &&
            ignoreElement.ValueKind == JsonValueKind.Array)
        {
            ignore.AddRange(ignoreElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => s.Length > 0));
        }

        return new ShadowTolerance(numeric, ignore);
    }

    private static int LineOfId(string tableText, string id)
    {
        var line = 1;
        foreach (var raw in tableText.Split('\n'))
        {
            if (Regex.IsMatch(raw, "\"id\"\\s*:\\s*\"" + Regex.Escape(id) + "\""))
            {
                return line;
            }

            line++;
        }

        return 1;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static decimal? ReadNumber(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
        {
            return number;
        }

        return null;
    }
}
