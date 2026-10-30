using System.Text.Json;

namespace Sfg.Toolkit.DrillHarness;

/// <summary>
/// The drills configuration (drills.json) — the evidence harness's required
/// input. Parsed with loud errors: the staleness window must be a positive
/// integer, and every referenced file must be named in the config. File paths
/// inside the config resolve relative to the config file's directory, so a
/// suite can be run from any working directory.
/// </summary>
public sealed class DrillHarnessConfig
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

    /// <summary>The judge configuration id, published with every report.</summary>
    public string Judge { get; init; } = string.Empty;

    /// <summary>A scenario whose latest drill is older than this is stale; default 180 days.</summary>
    public int StalenessDays { get; init; } = 180;

    public string ScenariosPath { get; init; } = string.Empty;

    public string EvidencePath { get; init; } = string.Empty;

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public static DrillHarnessConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"drills config not found: {path ?? "<null>"} — pass it with --config drills.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"drills config is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("drills config must be a JSON object with a \"scenarios\" file");
            }

            var name = ReadString(root, "name") ?? throw new InvalidOperationException(
                "drills config needs a non-empty \"name\" — what this drill catalog rehearses");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("drills config \"name\" must be non-empty");
            }

            var judge = ReadString(root, "judge") ?? throw new InvalidOperationException(
                "drills config needs a non-empty \"judge\" — the judge configuration id, published with every report");
            if (string.IsNullOrWhiteSpace(judge))
            {
                throw new InvalidOperationException("drills config \"judge\" must be non-empty");
            }

            var stalenessDays = 180;
            if (root.TryGetProperty("stalenessDays", out var stalenessElement))
            {
                if (stalenessElement.ValueKind != JsonValueKind.Number ||
                    !stalenessElement.TryGetInt32(out stalenessDays) || stalenessDays < 1)
                {
                    throw new InvalidOperationException("\"stalenessDays\" must be a positive integer");
                }
            }

            var scenariosPath = ReadString(root, "scenarios") ?? throw new InvalidOperationException(
                "drills config needs a \"scenarios\" file — the drill catalog, committed with the suite");
            var evidencePath = ReadString(root, "evidence") ?? throw new InvalidOperationException(
                "drills config needs an \"evidence\" file — the recorded drill export to judge (override with --evidence)");

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
            return new DrillHarnessConfig
            {
                Name = name,
                ConfigPath = path,
                Raw = text,
                Judge = judge,
                StalenessDays = stalenessDays,
                ScenariosPath = Resolve(configDirectory, scenariosPath),
                EvidencePath = Resolve(configDirectory, evidencePath),
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
