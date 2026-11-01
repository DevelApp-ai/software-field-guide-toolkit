using System.Text.Json;

namespace Sfg.Toolkit.UnitEcon;

/// <summary>
/// The unit-economics configuration (unit-econ.json) — the extractor's
/// required input. Parsed with loud errors: the seeded flag is mandatory
/// (a demo estate must say it is seeded, a real one must say it is not),
/// the currency must be declared, and budgets are per-period thresholds at
/// the configured granularity. File paths inside the config resolve relative
/// to the config file's directory, so an estate can be analyzed from any
/// working directory.
/// </summary>
public sealed class UnitEconConfig
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Path the config was loaded from, for findings evidence.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Raw config text, for line-anchored citations.</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>1-based line of the first line containing the needle, for citations.</summary>
    public string Anchor(string needle)
    {
        var line = 1;
        using var reader = new StringReader(Raw);
        while (reader.ReadLine() is { } current)
        {
            if (current.Contains(needle, StringComparison.Ordinal))
            {
                return $"{Path.GetFileName(ConfigPath)}:{line}";
            }

            line++;
        }

        return Path.GetFileName(ConfigPath);
    }

    /// <summary>True when the estate's numbers are seeded demo data — the boards must say so.</summary>
    public bool Seeded { get; init; }

    /// <summary>The recording currency, published with every number.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>day, week, or month — the trend and budget granularity; default month.</summary>
    public string Granularity { get; init; } = "month";

    public string BillingPath { get; init; } = string.Empty;

    public string AllocationsPath { get; init; } = string.Empty;

    public string TelemetryPath { get; init; } = string.Empty;

    /// <summary>Per-tenant budget thresholds per period at the configured granularity.</summary>
    public IReadOnlyDictionary<string, double> Budgets { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public static UnitEconConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"unit-econ config not found: {path ?? "<null>"} — pass it with --config unit-econ.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"unit-econ config is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("unit-econ config must be a JSON object with billing, allocations, and telemetry files");
            }

            var name = ReadString(root, "name") ?? throw new InvalidOperationException(
                "unit-econ config needs a non-empty \"name\" — what this estate's unit economics measures");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("unit-econ config \"name\" must be non-empty");
            }

            // The seeded flag is mandatory: a demo estate must admit it is
            // seeded, and a real one must say so — the honesty is explicit,
            // never guessed.
            if (root.TryGetProperty("seeded", out var seededElement))
            {
                if (seededElement.ValueKind != JsonValueKind.True && seededElement.ValueKind != JsonValueKind.False)
                {
                    throw new InvalidOperationException(
                        "unit-econ config needs a boolean \"seeded\" — true when the numbers are demo data, false when they are recorded exports");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "unit-econ config needs a boolean \"seeded\" — a demo estate must say it is seeded, a real one must say it is not");
            }

            var currency = ReadString(root, "currency") ?? throw new InvalidOperationException(
                "unit-econ config needs a non-empty \"currency\" — every rendered number is labeled with it");
            if (string.IsNullOrWhiteSpace(currency))
            {
                throw new InvalidOperationException("unit-econ config \"currency\" must be non-empty");
            }

            var granularity = "month";
            if (root.TryGetProperty("granularity", out var granularityElement))
            {
                granularity = ReadString(root, "granularity");
                if (granularity is not ("day" or "week" or "month"))
                {
                    throw new InvalidOperationException(
                        "unit-econ config \"granularity\" must be one of day, week, or month — budgets are declared per period at this granularity");
                }
            }

            var billingPath = ReadString(root, "billing") ?? throw new InvalidOperationException(
                "unit-econ config needs a \"billing\" file — the recorded billing export to allocate");
            var allocationsPath = ReadString(root, "allocations") ?? throw new InvalidOperationException(
                "unit-econ config needs an \"allocations\" file — the per-tenant allocation manifest");
            var telemetryPath = ReadString(root, "telemetry") ?? throw new InvalidOperationException(
                "unit-econ config needs a \"telemetry\" file — the recorded request counts to price");

            var budgets = new Dictionary<string, double>(StringComparer.Ordinal);
            if (root.TryGetProperty("budgets", out var budgetsElement))
            {
                if (budgetsElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("unit-econ config \"budgets\" must be an object of tenant to per-period budget threshold");
                }

                foreach (var property in budgetsElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) ||
                        property.Value.ValueKind != JsonValueKind.Number ||
                        property.Value.GetDouble() <= 0)
                    {
                        throw new InvalidOperationException(
                            $"unit-econ config budget '{property.Name}' must be a positive number — the per-period spend threshold");
                    }

                    budgets[property.Name] = property.Value.GetDouble();
                }
            }

            var maxFindings = 50;
            if (root.TryGetProperty("maxFindings", out var maxElement))
            {
                if (maxElement.ValueKind != JsonValueKind.Number ||
                    !maxElement.TryGetInt32(out maxFindings) || maxFindings < 1)
                {
                    throw new InvalidOperationException("\"maxFindings\" must be a positive integer");
                }
            }

            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return new UnitEconConfig
            {
                Name = name,
                ConfigPath = path,
                Raw = text,
                Seeded = seededElement.GetBoolean(),
                Currency = currency,
                Granularity = granularity,
                BillingPath = Resolve(configDirectory, billingPath),
                AllocationsPath = Resolve(configDirectory, allocationsPath),
                TelemetryPath = Resolve(configDirectory, telemetryPath),
                Budgets = budgets,
                MaxFindings = maxFindings,
            };
        }
    }

    internal static string Resolve(string configDirectory, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(configDirectory, path);

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }
}
