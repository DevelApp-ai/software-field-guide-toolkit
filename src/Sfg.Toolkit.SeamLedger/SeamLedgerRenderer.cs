using System.Text;
using System.Text.Json;

namespace Sfg.Toolkit.SeamLedger;

public static class SeamLedgerRenderer
{
    public static string LedgerJson(SeamLedgerReport report)
    {
        var payload = new
        {
            schemaVersion = 1,
            tool = "sfg seam-ledger",
            root = report.RootName,
            seams = report.Seams.Select(s => new
            {
                name = s.Name,
                context = s.Context,
                kind = s.Kind,
                file = s.File,
                line = s.Line,
                implementations = s.Implementations,
                consumerContexts = s.ConsumerContexts,
                usageSites = s.UsageSites,
                defaultImplementation = s.DefaultImplementation,
                versioned = s.Versioned,
                speculativeGenerality = s.SpeculativeGenerality,
                evidence = s.Evidence is null
                    ? null
                    : new
                    {
                        variationAxis = s.Evidence.VariationAxis,
                        teams = s.Evidence.Teams,
                        designDoc = s.Evidence.DesignDoc,
                        complete = s.Evidence.IsComplete,
                    },
                evidenceComplete = s.EvidenceComplete,
                collapseReview = new
                {
                    due = s.CollapseReview.Due,
                    reasons = s.CollapseReview.Reasons,
                },
            }),
            summary = new
            {
                seams = report.Seams.Count,
                dueForCollapse = report.DueForCollapse,
                evidenceMissing = report.EvidenceMissing,
            },
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CollapseReviewMarkdown(SeamLedgerReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Seam collapse review — {report.RootName}");
        builder.AppendLine();
        builder.AppendLine(
            $"Seams: {report.Seams.Count} · due for a collapse decision: {report.DueForCollapse} · missing evidence: {report.EvidenceMissing}");
        builder.AppendLine();
        builder.AppendLine("Generated from code truth; regenerate after boundary-affecting changes.");
        builder.AppendLine();

        builder.AppendLine("## Due for a collapse decision");
        builder.AppendLine();
        var due = report.Seams.Where(s => s.CollapseReview.Due).ToArray();
        if (due.Length == 0)
        {
            builder.AppendLine("None — every seam carries cross-context demand or a second implementation.");
        }
        else
        {
            foreach (var seam in due)
            {
                builder.AppendLine(
                    $"- [ ] `{seam.Name}` — {seam.Context} — {string.Join("; ", seam.CollapseReview.Reasons)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Monitoring (not due)");
        builder.AppendLine();
        foreach (var seam in report.Seams.Where(s => !s.CollapseReview.Due))
        {
            builder.AppendLine(
                $"- `{seam.Name}` — {seam.Context} — {seam.Implementations.Count} implementation(s), {seam.ConsumerContexts.Count} consumer context(s)");
        }

        builder.AppendLine();
        builder.AppendLine("## Evidence bar");
        builder.AppendLine();
        builder.AppendLine(
            "Every seam kept open needs a stated variation axis, two named teams, and a design doc link:");
        builder.AppendLine();
        var missing = report.Seams.Where(s => !s.EvidenceComplete).ToArray();
        if (missing.Length == 0)
        {
            builder.AppendLine("All seams meet the evidence bar.");
        }
        else
        {
            foreach (var seam in missing)
            {
                builder.AppendLine($"- ⚠ `{seam.Name}` — {seam.Context} — missing or incomplete evidence entry");
            }
        }

        return builder.ToString();
    }
}
