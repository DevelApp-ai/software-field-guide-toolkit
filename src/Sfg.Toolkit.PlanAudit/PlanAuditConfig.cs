using System.Text.Json;

namespace Sfg.Toolkit.PlanAudit;

/// <summary>
/// One declared spec-to-code mapping: intent files under <see cref="Spec"/>
/// are implemented by code files under <see cref="Code"/> (path prefixes,
/// '/'-separated). Both ends are required — a one-sided mapping never
/// passes silently.
/// </summary>
public sealed record SpecCodeMap(string Spec, string Code);

/// <summary>
/// The plan-audit configuration (plan-audit.json) — the auditor's required
/// input. Parsed with loud errors: duplicate or overlapping spec ends,
/// overlapping code ends, and empty prefixes never pass silently.
/// </summary>
public sealed class PlanAuditConfig
{
    public string? Project { get; init; }

    public IReadOnlyList<SpecCodeMap> Map { get; init; } = Array.Empty<SpecCodeMap>();

    /// <summary>Code path prefixes that may change without a spec basis (build files, docs).</summary>
    public IReadOnlyList<string> AllowedUnmappedCode { get; init; } = Array.Empty<string>();

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public static PlanAuditConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"plan-audit config not found: {path ?? "<null>"} — pass it with --config plan-audit.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"plan-audit config is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("plan-audit config must be a JSON object with a \"map\" array");
            }

            if (!root.TryGetProperty("map", out var mapElement) ||
                mapElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "plan-audit config needs a \"map\" array — spec areas to the code areas that implement them");
            }

            var map = new List<SpecCodeMap>();
            foreach (var entry in mapElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every map entry must be a JSON object");
                }

                var spec = ReadString(entry, "spec");
                var code = ReadString(entry, "code");
                if (string.IsNullOrWhiteSpace(spec))
                {
                    throw new InvalidOperationException("every map entry needs a non-empty \"spec\" prefix");
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    throw new InvalidOperationException("every map entry needs a non-empty \"code\" prefix");
                }

                map.Add(new SpecCodeMap(Normalize(spec), Normalize(code)));
            }

            if (map.Count == 0)
            {
                throw new InvalidOperationException(
                    "plan-audit config declares no map entries — there is nothing to audit");
            }

            foreach (var group in map.GroupBy(m => m.Spec, StringComparer.Ordinal))
            {
                if (group.Select(m => m.Code).Distinct(StringComparer.Ordinal).Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"map declares '{group.Key}' with different code ends — ambiguous");
                }
            }

            // overlapping spec prefixes would make a spec file match two areas: loud, never a guess
            foreach (var left in map)
            {
                foreach (var right in map)
                {
                    if (left.Spec != right.Spec && PathOverlaps(left.Spec, right.Spec))
                    {
                        throw new InvalidOperationException(
                            $"map spec prefixes overlap: '{left.Spec}' and '{right.Spec}' — a spec file could match two areas");
                    }

                    if (left.Code != right.Code && PathOverlaps(left.Code, right.Code))
                    {
                        throw new InvalidOperationException(
                            $"map code prefixes overlap: '{left.Code}' and '{right.Code}' — a code file could match two areas");
                    }
                }
            }

            var allowed = new List<string>();
            if (root.TryGetProperty("allowedUnmappedCode", out var allowedElement))
            {
                if (allowedElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("\"allowedUnmappedCode\" must be an array of code path prefixes");
                }

                foreach (var entry in allowedElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                    {
                        throw new InvalidOperationException(
                            "every \"allowedUnmappedCode\" entry must be a non-empty path prefix");
                    }

                    allowed.Add(Normalize(entry.GetString()!));
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

            return new PlanAuditConfig
            {
                Project = ReadString(root, "project"),
                Map = map.OrderBy(m => m.Spec, StringComparer.Ordinal).ToArray(),
                AllowedUnmappedCode = allowed.OrderBy(a => a, StringComparer.Ordinal).ToArray(),
                MaxFindings = maxFindings,
            };
        }
    }

    /// <summary>The map entry whose spec prefix contains <paramref name="path"/>, or null.</summary>
    public SpecCodeMap? SpecMapFor(string path)
    {
        foreach (var entry in Map)
        {
            if (PathMatches(path, entry.Spec))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The map entry whose code prefix contains <paramref name="path"/>, or null.</summary>
    public SpecCodeMap? CodeMapFor(string path)
    {
        foreach (var entry in Map)
        {
            if (PathMatches(path, entry.Code))
            {
                return entry;
            }
        }

        return null;
    }

    public bool IsAllowedUnmappedCode(string path)
    {
        foreach (var prefix in AllowedUnmappedCode)
        {
            if (PathMatches(path, prefix))
            {
                return true;
            }
        }

        return false;
    }

    internal static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    internal static bool PathMatches(string path, string prefix)
    {
        path = Normalize(path);
        if (path == prefix)
        {
            return true;
        }

        return path.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static bool PathOverlaps(string left, string right) => PathMatches(left, right) || PathMatches(right, left);

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }
}
