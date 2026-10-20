using System.Text;
using System.Text.Json;
using Sfg.Toolkit.Core.Analysis;
using Sfg.Toolkit.Core.CodeTruth;
using Sfg.Toolkit.Core.Drawio;

namespace Sfg.Toolkit.ContextMap;

/// <summary>
/// Renders a boundary report three ways: an editable drawio context map in the
/// book's diagram conventions, a machine-readable JSON report, and a findings
/// digest in Markdown. All three are deterministic for a given scan.
/// </summary>
public static class ContextMapRenderer
{
    private static readonly Dictionary<ContextRole, (string Fill, string Stroke)> RoleColors = new()
    {
        [ContextRole.Host] = ("#dae8fc", "#6c8ebf"),
        [ContextRole.SharedKernel] = ("#fff2cc", "#d6b656"),
        [ContextRole.Plugin] = ("#ffe6cc", "#d79b00"),
        [ContextRole.Other] = ("#f5f5f5", "#666666"),
    };

    public static string Drawio(BoundaryReport report)
    {
        var doc = new DrawioDocument();
        var positions = Layout(report.Contexts, out var pageWidth, out var pageHeight);

        var index = 0;
        foreach (var context in report.Contexts)
        {
            var (fill, stroke) = RoleColors[context.Role];
            var position = positions[context.Name];
            var label = $"{context.Name}<br>{SfgConfig.RoleKeyword(context.Role)}";
            doc.AddNode(
                $"n{index + 1}",
                label,
                $"rounded=1;whiteSpace=wrap;html=1;fillColor={fill};strokeColor={stroke};",
                position.X,
                position.Y,
                220,
                60);
            index++;
        }

        index = 0;
        foreach (var edge in report.Edges)
        {
            var label = $"{edge.References} refs";
            var clean = edge.ReachArounds == 0 && edge.DirectionViolations == 0;
            if (!clean)
            {
                label += $"<br>bypasses: {edge.ReachArounds}";
                if (edge.DirectionViolations > 0)
                {
                    label += $" · forbidden: {edge.DirectionViolations}";
                }
            }

            var style = clean
                ? "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;strokeColor=#82b366;"
                : "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;strokeColor=#b85450;dashed=1;";
            doc.AddEdge($"e{index + 1}", NodeId(report, edge.From), NodeId(report, edge.To), label, style);
            index++;
        }

        return doc.ToXml(pageWidth, pageHeight);
    }

    public static string Json(BoundaryReport report)
    {
        var payload = new
        {
            schemaVersion = 1,
            tool = "sfg context-map",
            root = report.RootName,
            contexts = report.Contexts.Select(c => new
            {
                name = c.Name,
                role = SfgConfig.RoleKeyword(c.Role),
                projectFile = c.ProjectFile,
                targetFramework = c.TargetFramework,
                projectReferences = c.ProjectReferences,
            }),
            edges = report.Edges.Select(e => new
            {
                from = e.From,
                to = e.To,
                references = e.References,
                contractReferences = e.ContractReferences,
                reachArounds = e.ReachArounds,
                directionViolations = e.DirectionViolations,
                status = e.Status,
            }),
            findings = report.Findings.Select(f => new
            {
                severity = f.Severity,
                rule = f.Rule,
                message = f.Message,
                from = f.From,
                to = f.To,
                file = f.File,
                line = f.Line,
                symbol = f.Symbol,
            }),
            summary = new { errors = report.Errors, warnings = report.Warnings },
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string FindingsMarkdown(BoundaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Context map findings — {report.RootName}");
        builder.AppendLine();
        builder.AppendLine($"Contexts: {report.Contexts.Count} · edges: {report.Edges.Count} · findings: {report.Findings.Count} ({report.Errors} errors, {report.Warnings} warnings)");
        builder.AppendLine();
        builder.AppendLine("| Severity | Rule | Location | Symbol | Message |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"| {finding.Severity} | `{finding.Rule}` | `{finding.File}:{finding.Line}` | `{finding.Symbol}` | {finding.Message} |");
        }

        if (report.Findings.Count == 0)
        {
            builder.AppendLine("| (none) | | | | No boundary findings. |");
        }

        return builder.ToString();
    }

    private static string NodeId(BoundaryReport report, string contextName)
    {
        var index = 0;
        foreach (var context in report.Contexts)
        {
            if (context.Name == contextName)
            {
                return $"n{index + 1}";
            }

            index++;
        }

        return "n1";
    }

    private static Dictionary<string, (double X, double Y)> Layout(
        IReadOnlyList<ContextInfo> contexts,
        out int pageWidth,
        out int pageHeight)
    {
        var rows = new Dictionary<ContextRole, int>
        {
            [ContextRole.Host] = 0,
            [ContextRole.SharedKernel] = 1,
            [ContextRole.Plugin] = 2,
            [ContextRole.Other] = 3,
        };

        var columns = contexts
            .GroupBy(c => rows[c.Role])
            .ToDictionary(g => g.Key, g => g.Count());
        var maxColumns = Math.Max(1, columns.Values.Max());

        var result = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var counters = new Dictionary<int, int>();
        foreach (var context in contexts)
        {
            var row = rows[context.Role];
            counters.TryAdd(row, 0);
            var column = counters[row];
            counters[row]++;
            result[context.Name] = (40 + column * 260, 40 + row * 140);
        }

        pageWidth = Math.Max(1169, 40 + maxColumns * 260 + 40);
        pageHeight = Math.Max(826, 40 + 4 * 140 + 60);
        return result;
    }
}
