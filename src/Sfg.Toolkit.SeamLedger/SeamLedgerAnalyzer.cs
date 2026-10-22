using System.Text.RegularExpressions;
using Sfg.Toolkit.Core.CodeTruth;

namespace Sfg.Toolkit.SeamLedger;

public sealed record CollapseReview(bool Due, IReadOnlyList<string> Reasons);

public sealed record SeamEntry(
    string Name,
    string Context,
    string Kind,
    string File,
    int Line,
    IReadOnlyList<string> Implementations,
    IReadOnlyList<string> ConsumerContexts,
    int UsageSites,
    string? DefaultImplementation,
    bool Versioned,
    SeamEvidence? Evidence,
    bool SpeculativeGenerality,
    CollapseReview CollapseReview)
{
    public bool EvidenceComplete => Evidence is { IsComplete: true };
}

public sealed record SeamLedgerReport(
    string RootName,
    IReadOnlyList<SeamEntry> Seams)
{
    public int DueForCollapse => Seams.Count(s => s.CollapseReview.Due);
    public int EvidenceMissing => Seams.Count(s => !s.EvidenceComplete);
}

/// <summary>
/// The complexity-ledger rules from book issue #55: inventories seams (interfaces,
/// plugin contracts, event hooks) from code truth, counts consumers per seam,
/// flags one-implementation seams as speculative generality, and renders the
/// collapse-review checklist. The evidence bar is a required field linked to a
/// design doc — seams without evidence are listed, never guessed.
/// </summary>
public sealed class SeamLedgerAnalyzer
{
    private static readonly Regex GenericBase = new(
        @"^(?<interface>[A-Za-z_][A-Za-z0-9_]*)\s*<\s*(?<argument>[A-Za-z_][A-Za-z0-9_]*)\s*>",
        RegexOptions.Compiled);

    public SeamLedgerReport Analyze(
        ScanResult scan,
        SfgConfig config,
        IReadOnlyDictionary<string, SeamEvidence>? evidence = null)
    {
        evidence ??= new Dictionary<string, SeamEvidence>();
        var seamTypes = new SortedDictionary<string, SeamEntry>(StringComparer.Ordinal);
        var usageLines = scan.Usages
            .GroupBy(u => (u.Project, u.File, u.Line))
            .ToDictionary(g => g.Key, g => g.Select(u => u.Identifier).ToHashSet(StringComparer.Ordinal));

        // Interfaces (the seam currency) and plugin contracts.
        foreach (var type in scan.Types)
        {
            if (type.Kind != TypeKind.Interface || type.Accessibility != Accessibility.Public)
            {
                continue;
            }

            var kind = IsPluginContract(type, config) ? "plugin-contract" : "interface";
            AddSeam(seamTypes, scan, config, evidence, usageLines, type, kind);
        }

        // Event hooks: every type consumed through a configured event contract,
        // e.g. IEventHandler<OrderPlacedEvent> -> the seam is OrderPlacedEvent.
        foreach (var type in scan.Types)
        {
            foreach (var raw in type.BaseNamesRaw)
            {
                var match = GenericBase.Match(raw);
                if (!match.Success)
                {
                    continue;
                }

                var interfaceName = match.Groups["interface"].Value;
                if (!config.EventContracts.Contains(interfaceName, StringComparer.Ordinal))
                {
                    continue;
                }

                var eventName = match.Groups["argument"].Value;
                var eventTypes = scan.Types
                    .Where(t => t.Name == eventName)
                    .OrderBy(t => t.Project, StringComparer.Ordinal)
                    .ThenBy(t => t.File, StringComparer.Ordinal)
                    .ToList();
                if (eventTypes.Count == 0)
                {
                    continue;
                }

                if (!seamTypes.TryGetValue(eventName, out var existing) || existing.Kind == "interface")
                {
                    var eventType = eventTypes[0];
                    var handler = type.Name;
                    var entry = BuildEntry(scan, config, evidence, usageLines, eventType, "event-hook");
                    var implementations = entry.Implementations
                        .Concat(new[] { handler })
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(i => i, StringComparer.Ordinal)
                        .ToArray();
                    var withHandler = entry with { Implementations = implementations };
                    seamTypes[eventName] = withHandler;
                }
            }
        }

        return new SeamLedgerReport(scan.RootName, seamTypes.Values.ToArray());
    }

    private static bool IsPluginContract(TypeRecord type, SfgConfig config)
    {
        if (config.PluginContractInterfaces.Contains(type.Name, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var baseName in type.BaseNames)
        {
            if (config.PluginContractInterfaces.Contains(baseName, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddSeam(
        SortedDictionary<string, SeamEntry> seamTypes,
        ScanResult scan,
        SfgConfig config,
        IReadOnlyDictionary<string, SeamEvidence> evidence,
        Dictionary<(string Project, string File, int Line), HashSet<string>> usageLines,
        TypeRecord type,
        string kind)
    {
        seamTypes[type.Name] = BuildEntry(scan, config, evidence, usageLines, type, kind);
    }

    private static SeamEntry BuildEntry(
        ScanResult scan,
        SfgConfig config,
        IReadOnlyDictionary<string, SeamEvidence> evidence,
        Dictionary<(string Project, string File, int Line), HashSet<string>> usageLines,
        TypeRecord type,
        string kind)
    {
        var implementations = scan.Types
            .Where(t => t.Kind is TypeKind.Class or TypeKind.Record &&
                        t.BaseNames.Contains(type.Name, StringComparer.Ordinal))
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var consumerContexts = scan.Usages
            .Where(u => u.Identifier == type.Name && u.Project != type.Project)
            .Select(u => u.Project)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var usageSites = scan.Usages.Count(u => u.Identifier == type.Name);

        string? defaultImplementation = FindRegisteredDefault(scan, usageLines, type.Name, implementations)
            ?? FindConventionalDefault(type.Name, implementations);

        var versioned = type.Members.Any(m =>
            string.Equals(m, "Version", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Version", StringComparison.OrdinalIgnoreCase));

        evidence.TryGetValue(type.Name, out var seamEvidence);

        var speculative = kind != "event-hook" && implementations.Length <= 1;

        var reasons = new List<string>();
        if (implementations.Length == 0)
        {
            reasons.Add("no implementations found");
        }

        if (consumerContexts.Length == 0)
        {
            reasons.Add("no cross-context consumers");
        }
        else if (consumerContexts.Length == 1 && implementations.Length <= 1)
        {
            reasons.Add("single consumer context");
        }

        if (implementations.Length == 1)
        {
            reasons.Add("single implementation");
        }

        var due = kind == "event-hook"
            ? consumerContexts.Length == 0 && implementations.Length <= 1
            : consumerContexts.Length == 0 || (implementations.Length <= 1 && consumerContexts.Length <= 1);

        return new SeamEntry(
            type.Name,
            type.Project,
            kind,
            type.File,
            type.Line,
            implementations,
            consumerContexts,
            usageSites,
            defaultImplementation,
            versioned,
            seamEvidence,
            speculative,
            new CollapseReview(due, reasons.Distinct().OrderBy(r => r, StringComparer.Ordinal).ToArray()));
    }

    private static string? FindRegisteredDefault(
        ScanResult scan,
        Dictionary<(string Project, string File, int Line), HashSet<string>> usageLines,
        string seamName,
        IReadOnlyList<string> implementations)
    {
        foreach (var usage in scan.Usages)
        {
            if (usage.Identifier != seamName)
            {
                continue;
            }

            if (!usageLines.TryGetValue((usage.Project, usage.File, usage.Line), out var lineIdentifiers))
            {
                continue;
            }

            var registered = implementations
                .Where(i => lineIdentifiers.Contains(i))
                .OrderBy(i => i, StringComparer.Ordinal)
                .FirstOrDefault();
            if (registered is not null)
            {
                return $"{registered} (registered at {usage.File}:{usage.Line})";
            }
        }

        return null;
    }

    private static string? FindConventionalDefault(string seamName, IReadOnlyList<string> implementations)
    {
        if (!seamName.StartsWith('I') || seamName.Length < 2)
        {
            return null;
        }

        var stripped = seamName[1..];
        return implementations
            .Where(i => string.Equals(i, stripped, StringComparison.Ordinal) || i.StartsWith(stripped, StringComparison.Ordinal))
            .OrderBy(i => i, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
