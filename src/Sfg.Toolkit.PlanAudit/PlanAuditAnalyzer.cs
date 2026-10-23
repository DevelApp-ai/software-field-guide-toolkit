using System.Text.Json;

namespace Sfg.Toolkit.PlanAudit;

/// <summary>One conformance finding: the two discrepancy classes plus the mapping warnings.</summary>
public sealed record PlanFinding(string Rule, string Severity, string Message, string? SpecFile, int? SpecLine, string? CodeFile, int? CodeLine);

/// <summary>One reconciled spec-to-code match with its provenance.</summary>
public sealed record PlanMatch(string SpecFile, int SpecLine, string CodeFile, int CodeLine, string Provenance, string? Note);

/// <summary>
/// The plan-conformance report (#64): every changed spec file from the
/// approved intent diff, every changed code file from the implementation
/// diff, the matches that reconcile them, and the findings — omission
/// (spec change with no generated change) and unapproved addition
/// (generated change with no spec basis) as errors, unmapped spec/code as
/// warnings. Findings are ranked errors-first, sorted, and capped.
/// </summary>
public sealed record PlanAuditReport(
    IReadOnlyList<DiffFile> SpecFiles,
    IReadOnlyList<DiffFile> CodeFiles,
    IReadOnlyList<PlanMatch> Matches,
    IReadOnlyList<PlanFinding> Findings,
    IReadOnlyList<PlanFinding> RenderedFindings,
    int Errors,
    int Warnings,
    int Cap)
{
    public bool IsValid => Findings.Count == 0;
}

/// <summary>
/// The conformance auditor: takes the approved spec change (the diff of
/// .ai/.test files in the intent PR) and the generated diff from the
/// implementation PR, reconciles them through the declared spec-to-code
/// map, and reports the two discrepancy classes with line anchors on both
/// sides. Machine-checkable by construction (changed-file coverage maps);
/// an optional semantic-matches file (externally produced, e.g. by an LLM
/// pass) reconciles what the coverage map cannot — every such match is
/// labeled and anchor-validated, never a silent guess.
/// </summary>
public static class PlanAuditAnalyzer
{
    public static PlanAuditReport Analyze(
        PlanAuditConfig config,
        IReadOnlyList<DiffFile> specFiles,
        IReadOnlyList<DiffFile> codeFiles,
        IReadOnlyList<SemanticMatch>? semantic)
    {
        var matches = new List<PlanMatch>();
        var findings = new List<PlanFinding>();

        // machine matches: a changed spec file in area A + a changed code file
        // in the code area A maps to — the coverage-map reconciliation
        // diff paths are unique per file, so paths identify the change units
        var specMatched = new HashSet<string>(StringComparer.Ordinal);
        var codeMatched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specFiles)
        {
            var map = config.SpecMapFor(spec.Path);
            if (map is null)
            {
                continue;
            }

            foreach (var code in codeFiles)
            {
                if (!PathMatchesArea(code.Path, map.Code))
                {
                    continue;
                }

                matches.Add(new PlanMatch(spec.Path, spec.AddedLine, code.Path, code.AddedLine, "machine", null));
                specMatched.Add(spec.Path);
                codeMatched.Add(code.Path);
            }
        }

        // semantic matches: externally produced, labeled, and anchor-validated
        foreach (var semanticMatch in semantic ?? Array.Empty<SemanticMatch>())
        {
            var spec = specFiles.FirstOrDefault(f => f.Path == semanticMatch.SpecFile);
            if (spec is null)
            {
                throw new InvalidOperationException(
                    $"semantic match cites spec file '{semanticMatch.SpecFile}' — not a changed file in the approved spec diff");
            }

            var code = codeFiles.FirstOrDefault(f => f.Path == semanticMatch.CodeFile);
            if (code is null)
            {
                throw new InvalidOperationException(
                    $"semantic match cites code file '{semanticMatch.CodeFile}' — not a changed file in the implementation diff");
            }

            if (!IsAnchored(spec, semanticMatch.SpecLine))
            {
                throw new InvalidOperationException(
                    $"semantic match cites {semanticMatch.SpecFile}:{semanticMatch.SpecLine} — not a line the spec diff changes");
            }

            if (!IsAnchored(code, semanticMatch.CodeLine))
            {
                throw new InvalidOperationException(
                    $"semantic match cites {semanticMatch.CodeFile}:{semanticMatch.CodeLine} — not a line the code diff changes");
            }

            matches.Add(new PlanMatch(
                semanticMatch.SpecFile, semanticMatch.SpecLine,
                semanticMatch.CodeFile, semanticMatch.CodeLine,
                "semantic", semanticMatch.Note));
            specMatched.Add(spec.Path);
            codeMatched.Add(code.Path);
        }

        matches.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.SpecFile, b.SpecFile);
            if (c != 0) { return c; }
            c = a.SpecLine.CompareTo(b.SpecLine);
            if (c != 0) { return c; }
            c = string.CompareOrdinal(a.CodeFile, b.CodeFile);
            return c != 0 ? c : a.CodeLine.CompareTo(b.CodeLine);
        });

        // the two discrepancy classes (errors)
        foreach (var spec in specFiles.Where(f => !specMatched.Contains(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var map = config.SpecMapFor(spec.Path);
            if (map is null)
            {
                findings.Add(new PlanFinding(
                    "plan/unmapped-spec", "warning",
                    $"spec file '{spec.Path}' is changed but matches no map entry — the spec-to-code map does not cover this intent",
                    spec.Path, spec.AddedLine, null, null));
                continue;
            }

            findings.Add(new PlanFinding(
                "plan/omission", "error",
                $"approved spec change '{spec.Path}' (line {spec.AddedLine}) has no generated change under '{map.Code}' — the intent was never implemented",
                spec.Path, spec.AddedLine, null, null));
        }

        foreach (var code in codeFiles.Where(f => !codeMatched.Contains(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var map = config.CodeMapFor(code.Path);
            if (map is null)
            {
                if (config.IsAllowedUnmappedCode(code.Path))
                {
                    continue;
                }

                findings.Add(new PlanFinding(
                    "plan/unmapped-code", "warning",
                    $"code file '{code.Path}' matches no map entry and no allowed-unmapped prefix — no spec area can account for this change",
                    null, null, code.Path, code.AddedLine));
                continue;
            }

            findings.Add(new PlanFinding(
                "plan/unapproved-addition", "error",
                $"generated change '{code.Path}' (line {code.AddedLine}) has no spec basis under '{map.Spec}' — unapproved behavior (scope drift, invented feature, or hallucinated cleanup)",
                null, null, code.Path, code.AddedLine));
        }

        // rank errors first, then warnings; deterministic ordinal order
        var ordered = findings
            .OrderBy(f => f.Severity == "error" ? 0 : 1)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.SpecFile ?? f.CodeFile ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(f => f.SpecLine ?? f.CodeLine ?? 0)
            .ToArray();

        var errors = ordered.Count(f => f.Severity == "error");
        var warnings = ordered.Length - errors;
        var rendered = ordered.Length > config.MaxFindings ? ordered[..config.MaxFindings] : ordered;

        return new PlanAuditReport(
            specFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            codeFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            matches,
            ordered,
            rendered,
            errors,
            warnings,
            config.MaxFindings);
    }

    /// <summary>
    /// A semantic match as declared in the optional --semantic file: an
    /// externally produced spec-to-code correspondence (e.g. by an LLM pass),
    /// accepted only with valid anchors on both sides.
    /// </summary>
    public sealed record SemanticMatch(string SpecFile, int SpecLine, string CodeFile, int CodeLine, string? Note);

    public static IReadOnlyList<SemanticMatch> LoadSemantic(string? path)
    {
        if (path is null)
        {
            return Array.Empty<SemanticMatch>();
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"semantic matches file not found: {path}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"semantic matches file is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("matches", out var matchesElement) ||
                matchesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "semantic matches file needs a \"matches\" array — { \"spec\": \"file:line\", \"code\": \"file:line\" } entries");
            }

            var result = new List<SemanticMatch>();
            foreach (var entry in matchesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every semantic match must be a JSON object");
                }

                var spec = ReadAnchor(entry, "spec");
                var code = ReadAnchor(entry, "code");
                string? note = null;
                if (entry.TryGetProperty("note", out var noteElement))
                {
                    note = noteElement.ValueKind == JsonValueKind.String ? noteElement.GetString() : null;
                }

                result.Add(new SemanticMatch(spec.file, spec.line, code.file, code.line, note));
            }

            return result;
        }
    }

    private static (string file, int line) ReadAnchor(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"every semantic match needs a \"{name}\" anchor 'path:line'");
        }

        var anchor = value.GetString()!;
        var colon = anchor.LastIndexOf(':');
        if (colon < 1 || !int.TryParse(anchor[(colon + 1)..], out var line) || line < 1)
        {
            throw new InvalidOperationException($"semantic {name} anchor '{anchor}' must be 'path:line'");
        }

        return (anchor[..colon], line);
    }

    private static bool PathMatchesArea(string path, string prefix)
    {
        var normalized = PlanAuditConfig.Normalize(path);
        return normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static bool IsAnchored(DiffFile file, int line)
    {
        foreach (var hunk in file.Hunks)
        {
            if (line >= hunk.NewStart && line <= hunk.NewStart + hunk.Lines.Count)
            {
                return true;
            }
        }

        return false;
    }
}
