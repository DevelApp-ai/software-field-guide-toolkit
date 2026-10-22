using Sfg.Toolkit.Core.CodeTruth;

namespace Sfg.Toolkit.Core.Analysis;

public sealed record Finding(
    string Rule,
    string Severity,
    string Message,
    string From,
    string To,
    string File,
    int Line,
    string Symbol);

public sealed record DependencyEdge(
    string From,
    string To,
    int References,
    int ContractReferences,
    int ReachArounds,
    int DirectionViolations)
{
    public string Status => DirectionViolations > 0 ? "forbidden"
        : ReachArounds > 0 ? "bypassed"
        : "ok";
}

public sealed record BoundaryReport(
    string RootName,
    IReadOnlyList<ContextInfo> Contexts,
    IReadOnlyList<DependencyEdge> Edges,
    IReadOnlyList<Finding> Findings)
{
    public int Errors => Findings.Count(f => f.Severity == "error");
    public int Warnings => Findings.Count(f => f.Severity == "warning");
}

/// <summary>
/// The context-map rules from book issue #54: reach-arounds around published
/// contracts, forbidden directions, shared-kernel sprawl, and orphaned seams.
/// Every finding carries file/line evidence from the scan.
/// </summary>
public sealed class BoundaryAnalyzer
{
    public BoundaryReport Analyze(ScanResult scan, SfgConfig config)
    {
        var ownership = BuildOwnership(scan, out var ambiguous);
        var crossProjectUsers = BuildCrossProjectUsers(scan);

        var edgeTally = new Dictionary<(string From, string To), EdgeTally>();
        var findings = new List<Finding>();
        var seenFindings = new HashSet<(string Rule, string File, int Line, string Symbol)>();

        foreach (var usage in scan.Usages)
        {
            if (!ownership.TryGetValue(usage.Identifier, out var target) &&
                !ownership.TryGetValue(usage.Identifier + "Attribute", out target))
            {
                continue;
            }

            if (target.Project == usage.Project)
            {
                continue;
            }

            var isContract = IsPublishedContract(target, config);
            var forbidden = IsForbiddenDirection(usage.Project, target.Project, config);

            var key = (usage.Project, target.Project);
            var tally = edgeTally.TryGetValue(key, out var existing) ? existing : new EdgeTally(0, 0, 0, 0);
            edgeTally[key] = new EdgeTally(
                tally.References + 1,
                tally.Contracts + (isContract ? 1 : 0),
                tally.ReachArounds + (isContract ? 0 : 1),
                tally.Violations + (forbidden ? 1 : 0));

            if (!isContract)
            {
                Add(findings, seenFindings, new Finding(
                    "boundary/reach-around",
                    "warning",
                    $"{usage.Project} uses {target.Project}.{target.Name} directly; it sits outside the published contract surface ({DescribeSurface(target, config)}) — route through the context's contract instead",
                    usage.Project,
                    target.Project,
                    usage.File,
                    usage.Line,
                    target.Name));
            }

            if (forbidden)
            {
                Add(findings, seenFindings, new Finding(
                    "boundary/direction-violation",
                    "error",
                    $"{usage.Project} consumes {target.Project}.{target.Name} across a forbidden direction ({RoleLabel(usage.Project, config)} -> {RoleLabel(target.Project, config)}); this is a boundary rule violation",
                    usage.Project,
                    target.Project,
                    usage.File,
                    usage.Line,
                    target.Name));
            }
        }

        foreach (var type in scan.Types)
        {
            if (!IsPublishedContract(type, config))
            {
                continue;
            }

            if (!crossProjectUsers.TryGetValue(type.Name, out var consumers) || consumers.Count == 0)
            {
                Add(findings, seenFindings, new Finding(
                    "boundary/orphaned-seam",
                    "warning",
                    $"{type.Project}.{type.Name} is a published contract with no consumers outside its own context — collapse it, publish it deliberately, or delete it",
                    type.Project,
                    type.Project,
                    type.File,
                    type.Line,
                    type.Name));
            }
            else if (config.RoleOf(type.Project) == ContextRole.SharedKernel && consumers.Count == 1)
            {
                Add(findings, seenFindings, new Finding(
                    "boundary/shared-kernel-sprawl",
                    "warning",
                    $"shared-kernel contract {type.Name} is consumed by exactly one context ({consumers[0]}); move it into that context or publish it to more consumers",
                    type.Project,
                    consumers[0],
                    type.File,
                    type.Line,
                    type.Name));
            }
        }

        var edges = edgeTally
            .OrderBy(kvp => kvp.Key.From, StringComparer.Ordinal)
            .ThenBy(kvp => kvp.Key.To, StringComparer.Ordinal)
            .Select(kvp => new DependencyEdge(
                kvp.Key.From,
                kvp.Key.To,
                kvp.Value.References,
                kvp.Value.Contracts,
                kvp.Value.ReachArounds,
                kvp.Value.Violations))
            .ToArray();

        var orderedFindings = findings
            .OrderBy(f => f.Severity == "error" ? 0 : 1)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Symbol, StringComparer.Ordinal)
            .ToArray();

        return new BoundaryReport(scan.RootName, scan.Contexts, edges, orderedFindings);
    }

    public static bool IsPublishedContract(TypeRecord type, SfgConfig config)
    {
        if (type.Accessibility != Accessibility.Public)
        {
            return false;
        }

        if (type.Kind == TypeKind.Interface)
        {
            return true;
        }

        foreach (var pattern in config.ContractNamespaces)
        {
            var needle = pattern.Trim('.').ToLowerInvariant();
            if (needle.Length > 0 && type.Namespace.ToLowerInvariant().Contains(needle))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct EdgeTally(int References, int Contracts, int ReachArounds, int Violations);

    private static void Add(
        List<Finding> findings,
        HashSet<(string Rule, string File, int Line, string Symbol)> seen,
        Finding finding)
    {
        var key = (finding.Rule, finding.File, finding.Line, finding.Symbol);
        if (seen.Add(key))
        {
            findings.Add(finding);
        }
    }

    private static Dictionary<string, TypeRecord> BuildOwnership(ScanResult scan, out HashSet<string> ambiguous)
    {
        ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, TypeRecord>(StringComparer.Ordinal);
        foreach (var group in scan.Types.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            if (group.Select(t => t.Project).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                ambiguous.Add(group.Key);
            }
            else
            {
                result[group.Key] = group.OrderBy(t => t.File, StringComparer.Ordinal).First();
            }
        }

        foreach (var name in ambiguous)
        {
            result.Remove(name);
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildCrossProjectUsers(ScanResult scan)
    {
        var byType = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var ownership = scan.Types
            .Where(t => t.Accessibility == Accessibility.Public)
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Select(t => t.Project).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Project, StringComparer.Ordinal);

        foreach (var usage in scan.Usages)
        {
            if (!ownership.TryGetValue(usage.Identifier, out var ownerProject) ||
                ownerProject == usage.Project)
            {
                continue;
            }

            if (!byType.TryGetValue(usage.Identifier, out var consumers))
            {
                consumers = new SortedSet<string>(StringComparer.Ordinal);
                byType[usage.Identifier] = consumers;
            }

            consumers.Add(usage.Project);
        }

        return byType.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private bool IsForbiddenDirection(string from, string to, SfgConfig config)
    {
        var fromRole = config.RoleOf(from);
        var toRole = config.RoleOf(to);
        if (from == to)
        {
            return false;
        }

        foreach (var pattern in config.ForbiddenEdges)
        {
            var separator = pattern.IndexOf("->", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var left = pattern[..separator].Trim();
            var right = pattern[(separator + 2)..].Trim();
            if (MatchesSide(left, from, fromRole) && MatchesSide(right, to, toRole))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSide(string side, string project, ContextRole role)
    {
        if (string.Equals(side, project, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(side, SfgConfig.RoleKeyword(role), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return role == ContextRole.SharedKernel &&
               string.Equals(side, "sharedkernel", StringComparison.OrdinalIgnoreCase);
    }

    private static string RoleLabel(string project, SfgConfig config) => SfgConfig.RoleKeyword(config.RoleOf(project));

    private static string DescribeSurface(TypeRecord type, SfgConfig config)
    {
        if (type.Accessibility != Accessibility.Public)
        {
            return "internal";
        }

        var patterns = string.Join(", ", config.ContractNamespaces);
        return $"public but outside the contract namespaces [{patterns}]";
    }
}
