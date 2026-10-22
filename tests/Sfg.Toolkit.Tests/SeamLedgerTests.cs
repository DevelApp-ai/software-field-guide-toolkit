using Sfg.Toolkit.Core.CodeTruth;
using Sfg.Toolkit.SeamLedger;
using Xunit;

namespace Sfg.Toolkit.Tests;

public sealed class SeamLedgerTests
{
    private static (SeamLedgerReport Report, TestCodebase Codebase) Analyze(string? evidenceJson = null)
    {
        var codebase = new TestCodebase();
        var scan = CodeTruthScanner.Scan(codebase.Root);
        var evidence = evidenceJson is null
            ? null
            : Path.Combine(codebase.Root, "evidence.json");
        if (evidenceJson is not null)
        {
            codebase.Write("evidence.json", evidenceJson);
        }

        var report = new SeamLedgerAnalyzer().Analyze(scan, SfgConfig.Default, EvidenceStore.Load(evidence));
        return (report, codebase);
    }

    [Fact]
    public void LedgerContainsInterfacesPluginContractsAndEventHooks()
    {
        var (report, codebase) = Analyze();
        try
        {
            Assert.Contains(report.Seams, s => s.Name == "IRefund" && s.Kind == "interface");
            Assert.Contains(report.Seams, s => s.Name == "IPluginModule" && s.Kind == "plugin-contract");
            Assert.Contains(report.Seams, s => s.Name == "OrderPlacedEvent" && s.Kind == "event-hook");
            Assert.Contains(report.Seams, s => s.Name == "IOrdering");
            Assert.Contains(report.Seams, s => s.Name == "ITime");
            Assert.Contains(report.Seams, s => s.Name == "IAudit");
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void ConsumerCountsAndContextsAreCountedFromCode()
    {
        var (report, codebase) = Analyze();
        try
        {
            var refund = report.Seams.Single(s => s.Name == "IRefund");
            Assert.Equal("Demo.PluginA", refund.Context);
            Assert.Contains("RefundService", refund.Implementations);
            Assert.Equal(new[] { "Demo.PluginB" }, refund.ConsumerContexts);
            Assert.True(refund.UsageSites > 0);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void SingleImplementationSeam_IsFlaggedSpeculativeGeneralityAndDue()
    {
        var (report, codebase) = Analyze();
        try
        {
            var refund = report.Seams.Single(s => s.Name == "IRefund");
            Assert.True(refund.SpeculativeGenerality);
            Assert.True(refund.CollapseReview.Due);
            Assert.Contains("single implementation", refund.CollapseReview.Reasons);
            Assert.Contains("single consumer context", refund.CollapseReview.Reasons);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void RegisteredDefaultImplementation_IsDetectedWithEvidence()
    {
        var (report, codebase) = Analyze();
        try
        {
            var refund = report.Seams.Single(s => s.Name == "IRefund");
            Assert.NotNull(refund.DefaultImplementation);
            Assert.Contains("RefundService", refund.DefaultImplementation);
            Assert.Contains("registered at", refund.DefaultImplementation);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void PluginContractVersionMember_MarksSeamVersioned()
    {
        var (report, codebase) = Analyze();
        try
        {
            var pluginModule = report.Seams.Single(s => s.Name == "IPluginModule");
            Assert.True(pluginModule.Versioned);
            Assert.Equal("plugin-contract", pluginModule.Kind);
            Assert.Contains("OrderModule", pluginModule.Implementations);

            var ordering = report.Seams.Single(s => s.Name == "IOrdering");
            Assert.False(ordering.Versioned);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void EventHookSeamCarriesHandlerImplementations()
    {
        var (report, codebase) = Analyze();
        try
        {
            var orderPlaced = report.Seams.Single(s => s.Name == "OrderPlacedEvent");
            Assert.Equal("event-hook", orderPlaced.Kind);
            Assert.Contains("OrderingService", orderPlaced.Implementations);
            Assert.Equal("Demo.PluginA", orderPlaced.Context);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void CollapseReview_RendersDueSeamsAsChecklist()
    {
        var (report, codebase) = Analyze();
        try
        {
            var markdown = SeamLedgerRenderer.CollapseReviewMarkdown(report);
            Assert.Contains("- [ ] `IRefund`", markdown);
            Assert.Contains("## Due for a collapse decision", markdown);
            Assert.Contains("## Evidence bar", markdown);
            Assert.Contains("missing or incomplete evidence", markdown);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void EvidenceBar_CompleteEntrySatisfiesMissingWarning()
    {
        var evidence = """
            {
              "IOrdering": {
                "variationAxis": "order placement channel (manual, api, bulk)",
                "teams": ["Orders", "Sales"],
                "designDoc": "https://example.invalid/design/ordering-seam"
              }
            }
            """;
        var (report, codebase) = Analyze(evidence);
        try
        {
            var ordering = report.Seams.Single(s => s.Name == "IOrdering");
            Assert.True(ordering.EvidenceComplete);
            Assert.NotNull(ordering.Evidence);
            Assert.Equal(2, ordering.Evidence.Teams.Count);
            Assert.Equal("https://example.invalid/design/ordering-seam", ordering.Evidence.DesignDoc);

            var markdown = SeamLedgerRenderer.CollapseReviewMarkdown(report);
            Assert.DoesNotContain("- ⚠ `IOrdering`", markdown);
            Assert.Contains("- ⚠ `IRefund`", markdown);

            var json = SeamLedgerRenderer.LedgerJson(report);
            Assert.Contains("\"evidenceComplete\": true", json);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void LedgerOutputIsDeterministic()
    {
        var codebase = new TestCodebase();
        try
        {
            var report1 = new SeamLedgerAnalyzer().Analyze(CodeTruthScanner.Scan(codebase.Root), SfgConfig.Default);
            var report2 = new SeamLedgerAnalyzer().Analyze(CodeTruthScanner.Scan(codebase.Root), SfgConfig.Default);
            Assert.Equal(SeamLedgerRenderer.LedgerJson(report1), SeamLedgerRenderer.LedgerJson(report2));
            Assert.Equal(SeamLedgerRenderer.CollapseReviewMarkdown(report1), SeamLedgerRenderer.CollapseReviewMarkdown(report2));
        }
        finally
        {
            codebase.Dispose();
        }
    }
}
