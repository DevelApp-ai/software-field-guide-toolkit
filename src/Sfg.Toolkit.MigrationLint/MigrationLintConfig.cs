using System.Text.Json;

namespace Sfg.Toolkit.MigrationLint;

/// <summary>One pass of the compatibility-gates ledger (migration-gates.json):
/// the recorded evidence that a contract step (a removal or a tightening) was
/// gated — the gate fails builds, not reviews.</summary>
public sealed record GatePass
{
    /// <summary>The emitted object the pass covers: "Table" or "Table.Column".</summary>
    public required string Object { get; init; }

    /// <summary>The compatibility gate the consumers passed (e.g. "forward-compat").</summary>
    public required string Gate { get; init; }

    /// <summary>When the expand step happened (yyyy-MM-dd) — the window opens.</summary>
    public DateOnly? ExpandedAt { get; init; }

    /// <summary>When the object was measured cold (yyyy-MM-dd) — the window closes.
    /// A pass with no coldAt is mid-window: legal, until something is removed.</summary>
    public DateOnly? ColdAt { get; init; }

    /// <summary>The consumers the gate passed for — never empty.</summary>
    public required IReadOnlyList<string> Consumers { get; init; }

    public bool WindowMeasured => ExpandedAt is not null && ColdAt is not null;
}

/// <summary>The committed gates ledger. Absent is legal (nothing was removed yet);
/// malformed never passes silently.</summary>
public sealed class GatesLedger
{
    public required string Path { get; init; }

    public required string Raw { get; init; }

    public required IReadOnlyList<GatePass> Passes { get; init; }

    public GatePass? PassOf(string objectName) =>
        Passes.FirstOrDefault(p => string.Equals(p.Object, objectName, StringComparison.Ordinal));

    /// <summary>1-based file:line anchor of the first line containing the needle.</summary>
    public string Anchor(string needle)
    {
        var line = 1;
        using var reader = new StringReader(Raw);
        while (reader.ReadLine() is { } current)
        {
            if (current.Contains(needle, StringComparison.Ordinal))
            {
                return $"{System.IO.Path.GetFileName(Path)}:{line}";
            }

            line++;
        }

        // the ledger exists but the needle is absent — cite its first line so
        // every finding keeps the file:line contract even when the pass is missing
        return $"{System.IO.Path.GetFileName(Path)}:1";
    }
}

/// <summary>The linter's optional configuration (migration-lint.json). Defaults:
/// migrations live in <root>/Migrations, the gates ledger is <root>/migration-gates.json.
/// Paths inside the config resolve relative to the root, so the same config works
/// from any working directory.</summary>
public sealed class MigrationLintConfig
{
    /// <summary>The repository root the paths resolve against.</summary>
    public required string Root { get; init; }

    public string MigrationsDir { get; init; } = "Migrations";

    public string GatesPath { get; init; } = "migration-gates.json";

    /// <summary>Verbatim gates path from --gates: overrides config and default,
    /// resolves against the working directory (CI variant passes live here).</summary>
    public string? GatesOverride { get; init; }

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public string MigrationsFullPath => Path.Combine(Root, MigrationsDir);

    public string GatesFullPath => GatesOverride ?? Path.Combine(Root, GatesPath);

    public static MigrationLintConfig Load(string? path, string root, string? gatesOverride)
    {
        var config = new MigrationLintConfig { Root = root };
        if (path is not null)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"migration-lint config not found: {path}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"migration-lint config is not valid JSON: {exception.Message}");
            }

            using (document)
            {
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("migration-lint config must be a JSON object");
                }

                string? migrations = null;
                if (element.TryGetProperty("migrations", out var migrationsElement))
                {
                    if (migrationsElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(migrationsElement.GetString()))
                    {
                        throw new InvalidOperationException("\"migrations\" must be a non-empty string — the directory the migration files live in");
                    }

                    migrations = migrationsElement.GetString();
                }

                string? gates = null;
                if (element.TryGetProperty("gates", out var gatesElement))
                {
                    if (gatesElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(gatesElement.GetString()))
                    {
                        throw new InvalidOperationException("\"gates\" must be a non-empty string — the compatibility-gates ledger file");
                    }

                    gates = gatesElement.GetString();
                }

                var maxFindings = 50;
                if (element.TryGetProperty("maxFindings", out var maxElement))
                {
                    if (maxElement.ValueKind != JsonValueKind.Number ||
                        !maxElement.TryGetInt32(out maxFindings) || maxFindings < 1)
                    {
                        throw new InvalidOperationException("\"maxFindings\" must be a positive integer");
                    }
                }

                config = new MigrationLintConfig
                {
                    Root = root,
                    MigrationsDir = migrations ?? "Migrations",
                    GatesPath = gates ?? "migration-gates.json",
                    MaxFindings = maxFindings,
                };
            }
        }

        // --gates overrides both the config and the default, verbatim; the CI
        // variant ledgers live in the repo's demo/broken/ and are passed as-is
        if (gatesOverride is not null)
        {
            config = new MigrationLintConfig
            {
                Root = config.Root,
                MigrationsDir = config.MigrationsDir,
                GatesPath = config.GatesPath,
                GatesOverride = gatesOverride,
                MaxFindings = config.MaxFindings,
            };
        }

        return config;
    }

    /// <summary>Loads the gates ledger. An absent file is not an error — a history
    /// that never removes anything needs no ledger; the findings name it when a
    /// contract step appears anyway.</summary>
    public GatesLedger? LoadGates()
    {
        if (!File.Exists(GatesFullPath))
        {
            return null;
        }

        var raw = File.ReadAllText(GatesFullPath);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"gates ledger is not valid JSON: {GatesPath}: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("passes", out var passes) || passes.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"gates ledger needs a \"passes\" array: {GatesPath}");
            }

            var list = new List<GatePass>();
            foreach (var entry in passes.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every gate pass must be a JSON object");
                }

                if (!entry.TryGetProperty("object", out var objectElement) ||
                    objectElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(objectElement.GetString()))
                {
                    throw new InvalidOperationException("every gate pass needs a non-empty \"object\" (\"Table\" or \"Table.Column\")");
                }

                var objectName = objectElement.GetString()!;
                if (!objectName.Contains('.') || objectName.StartsWith('.') || objectName.EndsWith('.'))
                {
                    throw new InvalidOperationException(
                        $"gate pass object '{objectName}' must be \"Table\" or \"Table.Column\" — the emitted object the gate covers");
                }

                var gate = ReadString(entry, "gate");
                if (gate is null)
                {
                    throw new InvalidOperationException(
                        $"gate pass for '{objectName}' needs a non-empty \"gate\" — the compatibility gate the consumers passed (e.g. \"forward-compat\")");
                }

                DateOnly? expandedAt = ReadDate(entry, objectName, "expandedAt");
                DateOnly? coldAt = ReadDate(entry, objectName, "coldAt");

                var consumers = new List<string>();
                if (!entry.TryGetProperty("consumers", out var consumersElement) ||
                    consumersElement.ValueKind != JsonValueKind.Array ||
                    consumersElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException(
                        $"gate pass for '{objectName}' needs a non-empty \"consumers\" array — the gate passed for named consumers, never nobody");
                }

                foreach (var consumer in consumersElement.EnumerateArray())
                {
                    if (consumer.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(consumer.GetString()))
                    {
                        throw new InvalidOperationException(
                            $"gate pass for '{objectName}' has an empty consumer name — every consumer must be a non-empty string");
                    }

                    consumers.Add(consumer.GetString()!);
                }

                if (expandedAt is { } expanded && coldAt is { } cold && cold < expanded)
                {
                    // an inverted window may be recorded mid-history, but it is
                    // never a measured window — the analyzer names it when a
                    // removal appears against it
                }

                list.Add(new GatePass
                {
                    Object = objectName,
                    Gate = gate,
                    ExpandedAt = expandedAt,
                    ColdAt = coldAt,
                    Consumers = consumers.ToArray(),
                });
            }

            foreach (var group in list.GroupBy(p => p.Object, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate gate pass for '{group.Key}' — one pass per emitted object, ambiguous is never allowed");
                }
            }

            return new GatesLedger { Path = GatesFullPath, Raw = raw, Passes = list };
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        return null;
    }

    private static DateOnly? ReadDate(JsonElement element, string objectName, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(
                value.GetString(), "yyyy-MM-dd", out var date))
        {
            throw new InvalidOperationException(
                $"gate pass for '{objectName}' has an invalid \"{name}\" — dates are yyyy-MM-dd");
        }

        return date;
    }
}
