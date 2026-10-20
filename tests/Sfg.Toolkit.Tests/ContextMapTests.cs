using System.Xml.Linq;
using Sfg.Toolkit.ContextMap;
using Sfg.Toolkit.Core.Analysis;
using Sfg.Toolkit.Core.CodeTruth;
using Xunit;

namespace Sfg.Toolkit.Tests;

public sealed class ContextMapTests
{
    private static (BoundaryReport Report, TestCodebase Codebase) Analyze()
    {
        var codebase = new TestCodebase();
        var scan = CodeTruthScanner.Scan(codebase.Root);
        var report = new BoundaryAnalyzer().Analyze(scan, SfgConfig.Default);
        return (report, codebase);
    }

    [Fact]
    public void SeededReachAround_IsCaughtWithExactFileLine()
    {
        var (report, codebase) = Analyze();
        try
        {
            var expectedLine = codebase.LineOf("Demo.Host/Composition.cs", "public OrderRules Rules");
            var finding = report.Findings.Single(f =>
                f.Rule == "boundary/reach-around" &&
                f.From == "Demo.Host" &&
                f.Symbol == "OrderRules");
            Assert.Equal("Demo.Host/Composition.cs", finding.File);
            Assert.Equal(expectedLine, finding.Line);
            Assert.Equal("Demo.PluginA", finding.To);
            Assert.Equal("warning", finding.Severity);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void SeededDirectionViolation_IsCaughtWithExactFileLine()
    {
        var (report, codebase) = Analyze();
        try
        {
            var expectedLine = codebase.LineOf("Demo.PluginB/InventoryService.cs", "IRefund refunds");
            var finding = Assert.Single(
                report.Findings,
                f => f.Rule == "boundary/direction-violation" &&
                     f.From == "Demo.PluginB" &&
                     f.File == "Demo.PluginB/InventoryService.cs" &&
                     f.Line == expectedLine &&
                     f.Symbol == "IRefund");
            Assert.Equal("error", finding.Severity);
            Assert.Contains(
                report.Findings,
                f => f.Rule == "boundary/direction-violation" &&
                     f.Symbol == "IRefund" &&
                     f.Line == codebase.LineOf("Demo.PluginB/InventoryService.cs", "IRefund _refunds"));
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void OrphanedSeam_IsCaughtAtDeclaration()
    {
        var (report, codebase) = Analyze();
        try
        {
            var expectedLine = codebase.LineOf("Demo.SharedKernel/Contracts/CoreContracts.cs", "public interface IAudit");
            var finding = report.Findings.Single(f =>
                f.Rule == "boundary/orphaned-seam" &&
                f.Symbol == "IAudit");
            Assert.Equal("Demo.SharedKernel/Contracts/CoreContracts.cs", finding.File);
            Assert.Equal(expectedLine, finding.Line);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void SharedKernelSprawl_IsCaughtForSingleConsumerContract()
    {
        var (report, codebase) = Analyze();
        try
        {
            var expectedLine = codebase.LineOf("Demo.SharedKernel/Contracts/CoreContracts.cs", "public interface ITime");
            var finding = report.Findings.Single(f =>
                f.Rule == "boundary/shared-kernel-sprawl" &&
                f.Symbol == "ITime");
            Assert.Equal("Demo.PluginA", finding.To);
            Assert.Equal("Demo.SharedKernel/Contracts/CoreContracts.cs", finding.File);
            Assert.Equal(expectedLine, finding.Line);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void EdgesClassifyCleanAndBypassedRelations()
    {
        var (report, codebase) = Analyze();
        try
        {
            var hostToSharedKernel = report.Edges.Single(e => e.From == "Demo.Host" && e.To == "Demo.SharedKernel");
            Assert.Equal("ok", hostToSharedKernel.Status);
            Assert.True(hostToSharedKernel.ContractReferences > 0);
            Assert.Equal(0, hostToSharedKernel.ReachArounds);

            var pluginBToPluginA = report.Edges.Single(e => e.From == "Demo.PluginB" && e.To == "Demo.PluginA");
            Assert.Equal("forbidden", pluginBToPluginA.Status);
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void DrawioMap_IsWellFormedAndResolvesEdges()
    {
        var (report, codebase) = Analyze();
        try
        {
            var xml = ContextMapRenderer.Drawio(report);
            var document = XDocument.Parse(xml);
            var mxfile = document.Root;
            Assert.NotNull(mxfile);
            Assert.Equal("vibe", (string?)mxfile!.Attribute("agent"));

            var cells = mxfile.Descendants("mxCell").ToList();
            var ids = cells.Select(c => (string?)c.Attribute("id")).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());

            var vertexIds = cells.Where(c => (string?)c.Attribute("vertex") == "1")
                .Select(c => (string?)c.Attribute("id"))
                .ToHashSet();
            Assert.Equal(report.Contexts.Count, vertexIds.Count);

            foreach (var edge in cells.Where(c => (string?)c.Attribute("edge") == "1"))
            {
                Assert.Contains((string?)edge.Attribute("source"), vertexIds);
                Assert.Contains((string?)edge.Attribute("target"), vertexIds);
            }
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void OutputIsDeterministic_SameTreeByteIdentical()
    {
        var codebase = new TestCodebase();
        try
        {
            var scan1 = CodeTruthScanner.Scan(codebase.Root);
            var report1 = new BoundaryAnalyzer().Analyze(scan1, SfgConfig.Default);
            var scan2 = CodeTruthScanner.Scan(codebase.Root);
            var report2 = new BoundaryAnalyzer().Analyze(scan2, SfgConfig.Default);

            Assert.Equal(ContextMapRenderer.Json(report1), ContextMapRenderer.Json(report2));
            Assert.Equal(ContextMapRenderer.Drawio(report1), ContextMapRenderer.Drawio(report2));
            Assert.Equal(ContextMapRenderer.FindingsMarkdown(report1), ContextMapRenderer.FindingsMarkdown(report2));
        }
        finally
        {
            codebase.Dispose();
        }
    }

    [Fact]
    public void ContextsHaveExpectedRoles()
    {
        var (report, codebase) = Analyze();
        try
        {
            Assert.Equal(ContextRole.Host, report.Contexts.Single(c => c.Name == "Demo.Host").Role);
            Assert.Equal(ContextRole.SharedKernel, report.Contexts.Single(c => c.Name == "Demo.SharedKernel").Role);
            Assert.Equal(ContextRole.Plugin, report.Contexts.Single(c => c.Name == "Demo.PluginA").Role);
            Assert.Equal(ContextRole.Plugin, report.Contexts.Single(c => c.Name == "Demo.PluginB").Role);
        }
        finally
        {
            codebase.Dispose();
        }
    }
}
