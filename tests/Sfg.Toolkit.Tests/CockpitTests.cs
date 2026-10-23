using System.Text.Json;
using System.Xml.Linq;
using Sfg.Toolkit.Cockpit;
using Xunit;

namespace Sfg.Toolkit.Tests;

public sealed class CockpitTests
{
    private static (CockpitReport Report, MigrationConfig Config, StranglerAppFixture Fixture) Analyze(
        string? configPath = null, Action<StranglerAppFixture>? seed = null)
    {
        var fixture = new StranglerAppFixture();
        try
        {
            seed?.Invoke(fixture);
            var path = configPath ?? fixture.ConfigPath;
            var config = MigrationConfig.Load(path);
            var wired = PivotScanner.Scan(fixture.Root, config.RouteMethod);
            var report = new CockpitAnalyzer().Analyze(config, wired, Path.GetFileName(path));
            return (report, config, fixture);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static (CockpitReport Report, StranglerAppFixture Fixture) AnalyzeSeededTable(
        Func<StranglerAppFixture, string> seedTable)
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var path = seedTable(fixture);
            var config = MigrationConfig.Load(path);
            var wired = PivotScanner.Scan(fixture.Root, config.RouteMethod);
            return (new CockpitAnalyzer().Analyze(config, wired, Path.GetFileName(path)), fixture);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static (ShadowDiffSuite Suite, StranglerAppFixture Fixture) GenerateSuite(
        Func<StranglerAppFixture, string>? seedTable = null)
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var path = seedTable?.Invoke(fixture) ?? fixture.ConfigPath;
            var config = MigrationConfig.Load(path);
            var handlers = HandlerScanner.Scan(fixture.Root);
            var suite = ShadowDiffGenerator.Generate(fixture.Root, Path.Combine(fixture.Root, "out"), config, handlers);
            return (suite, fixture);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static string SuiteFile(ShadowDiffSuite suite, string name) => suite.Files.Single(f => f.Path == name).Content;

    [Fact]
    public void Config_LoadsRoutesWithDefaultsAndLineEvidence()
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var config = MigrationConfig.Load(fixture.ConfigPath);

            Assert.Equal("MapRoute", config.RouteMethod);
            Assert.Equal(100m, config.DefaultRetirementThreshold);
            Assert.Equal(3, config.DefaultSunsetPeriods);
            Assert.Equal(new[] { "orders.get", "orders.search", "shipping.quote" },
                config.Routes.Select(r => r.Id).ToArray());

            var search = config.Routes.Single(r => r.Id == "orders.search");
            Assert.Equal("strangling", search.State);
            Assert.Equal("LegacyOrdersSearch", search.Old);
            Assert.Equal("OrdersSearchService", search.New);
            Assert.Equal("Handle", search.Method);
            Assert.True(search.Shadow);
            Assert.Equal(new[] { 500L, 400L }, search.Traffic);
            Assert.Equal(0.01m, search.Tolerance!.Numeric["total"]);
            Assert.Equal(new[] { "servedBy" }, search.Tolerance.Ignore);

            var quote = config.Routes.Single(r => r.Id == "shipping.quote");
            Assert.Equal("strangled", quote.State);
            Assert.Equal(100m, quote.RetirementThreshold);
            Assert.Equal(3, quote.SunsetPeriods);
            Assert.Equal(
                fixture.LineOf("migration.json", "\"id\": \"shipping.quote\""),
                quote.Line);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("")]
    public void Config_RejectsUnknownStateLoudly(string state)
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var path = fixture.WriteTable("migration.bad-state.json", $$"""
                {
                  "routes": [
                    { "id": "orders.search", "state": "{{state}}", "old": "LegacyOrdersSearch", "new": "OrdersSearchService" }
                  ]
                }
                """);

            var error = Assert.Throws<InvalidOperationException>(() => MigrationConfig.Load(path));
            Assert.Contains("route 'orders.search'", error.Message);
            Assert.Contains("unknown state", error.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Config_RejectsDuplicateRouteIds()
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var path = fixture.WriteTable("migration.dup.json", """
                {
                  "routes": [
                    { "id": "orders.get", "state": "todo", "old": "LegacyOrdersGet" },
                    { "id": "orders.get", "state": "todo", "old": "LegacyOrdersGet" }
                  ]
                }
                """);

            var error = Assert.Throws<InvalidOperationException>(() => MigrationConfig.Load(path));
            Assert.Contains("'orders.get' 2 times", error.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Config_RejectsShadowRouteWithoutCases()
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var path = fixture.WriteTable("migration.shadowless.json", """
                {
                  "routes": [
                    { "id": "orders.search", "state": "strangling", "old": "LegacyOrdersSearch", "new": "OrdersSearchService", "shadow": true }
                  ]
                }
                """);

            var error = Assert.Throws<InvalidOperationException>(() => MigrationConfig.Load(path));
            Assert.Contains("route 'orders.search'", error.Message);
            Assert.Contains("\"old\", \"new\", and \"cases\"", error.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void PivotScan_FindsWiredRoutesWithFileLineEvidenceAndSkipsNonLiterals()
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var wired = PivotScanner.Scan(fixture.Root, "MapRoute");

            Assert.Equal(3, wired.Count);
            Assert.DoesNotContain(wired, w => w.Id == "promo.code");
            var search = wired.Single(w => w.Id == "orders.search");
            Assert.Equal("RouteTableSetup.cs", search.File);
            Assert.Equal(fixture.LineOf("RouteTableSetup.cs", "pivot.MapRoute(\"orders.search\")"), search.Line);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void HandlerScan_FillsProjectPathWithEvidence()
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var sites = HandlerScanner.Scan(fixture.Root);
            var service = sites.Single(s => s.Name == "OrdersSearchService");

            Assert.Equal("Demo.App.New", service.Namespace);
            Assert.Equal("New/OrdersSearchService.cs", service.File);
            Assert.EndsWith("Demo.App.csproj", service.ProjectPath);
            Assert.Equal(fixture.LineOf("New/OrdersSearchService.cs", "public sealed class OrdersSearchService"), service.Line);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Coverage_FlagsUnwiredRouteAsErrorWithTableEvidence()
    {
        var (report, fixture) = AnalyzeSeededTable(f => f.SeedUnwiredTable());
        try
        {
            var finding = Assert.Single(report.Findings, f => f.Rule == "migration/unwired-route");
            Assert.Equal("promo.code", finding.RouteId);
            Assert.Equal("error", finding.Severity);
            Assert.Equal(
                $"migration.unwired.json:{fixture.LineOf("migration.unwired.json", "\"id\": \"promo.code\"")}",
                finding.Evidence);
            Assert.Equal(1, report.Errors);
            Assert.False(report.Routes.Single(r => r.Id == "promo.code").Wired);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Coverage_FlagsUndeclaredWireAsWarningWithPivotEvidence()
    {
        var (report, _, fixture) = Analyze(seed: f => f.SeedUndeclaredWire());
        try
        {
            var finding = Assert.Single(report.Findings, f => f.Rule == "migration/undeclared-route");
            Assert.Equal("admin.audit", finding.RouteId);
            Assert.Equal("warning", finding.Severity);
            Assert.Equal(
                $"RouteTableExtra.cs:{fixture.LineOf("RouteTableExtra.cs", "pivot.MapRoute(\"admin.audit\")")}",
                finding.Evidence);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void SunsetTripwire_FiresWhenTrafficRetiredBelowThreshold()
    {
        var (report, _, fixture) = Analyze();
        try
        {
            var finding = Assert.Single(report.Findings);
            Assert.Equal("migration/sunset-window", finding.Rule);
            Assert.Equal("shipping.quote", finding.RouteId);
            Assert.Equal("warning", finding.Severity);
            Assert.Contains("sunset window open", finding.Message);
            Assert.Contains("delete the old handler", finding.Message);
            Assert.StartsWith("migration.json:", finding.Evidence);
            Assert.Equal(0, report.Errors);
            Assert.Equal(1, report.Warnings);

            var view = report.Routes.Single(r => r.Id == "shipping.quote");
            Assert.True(view.SunsetWindowOpen);
            Assert.Equal(new[] { 0L, 0L, 0L }, view.Traffic.TakeLast(3).ToArray());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void StrangleUnfinished_FiresWhenTrafficContradictsStrangled()
    {
        var (report, fixture) = AnalyzeSeededTable(f => f.SeedLyingTable());
        try
        {
            var finding = Assert.Single(report.Findings);
            Assert.Equal("migration/strangle-unfinished", finding.Rule);
            Assert.Equal("shipping.quote", finding.RouteId);
            Assert.Equal("error", finding.Severity);
            Assert.Contains("has not strangled this route", finding.Message);
            Assert.StartsWith("migration.lying.json:", finding.Evidence);
            Assert.Equal(1, report.Errors);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void SunsetTripwire_StaysSilentOnInsufficientHistory()
    {
        var (report, fixture) = AnalyzeSeededTable(f => f.SeedInsufficientHistoryTable());
        try
        {
            Assert.Empty(report.Findings);
            Assert.False(report.Routes.Single(r => r.Id == "shipping.quote").SunsetWindowOpen);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void SunsetTripwire_IgnoresStranglingAndTodoRoutes()
    {
        var (report, fixture) = AnalyzeSeededTable(f => f.SeedStranglingTrafficTable());
        try
        {
            Assert.Empty(report.Findings);
            Assert.All(report.Routes, r => Assert.False(r.SunsetWindowOpen));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void ShadowSuite_EmitsSixFilesWithHandlerAndCaseEvidence()
    {
        var (suite, fixture) = GenerateSuite();
        try
        {
            Assert.Equal(6, suite.Files.Count);
            Assert.Equal(
                new[] { "Cases.cs", "README.md", "RouteShadowProbes.cs", "ShadowDiff.csproj", "ShadowDiffHarness.cs", "manifest.json" },
                suite.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray());

            var probes = SuiteFile(suite, "RouteShadowProbes.cs");
            Assert.Contains("public void OrdersSearch_old_and_new_stay_within_tolerance()", probes);
            Assert.Contains("public void ShippingQuote_old_and_new_stay_within_tolerance()", probes);
            Assert.Contains("input => new LegacyOrdersSearch().Handle(input)", probes);
            Assert.Contains("input => new OrdersSearchService().Handle(input)", probes);
            Assert.Contains("(\"total\", 0.01m)", probes);
            Assert.Contains("\"servedBy\"", probes);

            var csproj = SuiteFile(suite, "ShadowDiff.csproj");
            Assert.Contains("Demo.App.csproj\" />", csproj);
            Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj);

            var cases = SuiteFile(suite, "Cases.cs");
            Assert.Contains("\"sku\": \"A-1001\"", cases);

            using var manifest = JsonDocument.Parse(SuiteFile(suite, "manifest.json"));
            var root = manifest.RootElement;
            Assert.Equal("sfg cockpit 0.5.0", root.GetProperty("generator").GetString());
            Assert.Equal("Demo.App.csproj", root.GetProperty("targetProject").GetString());
            Assert.Equal(2, root.GetProperty("shadowRoutes").GetArrayLength());
            var search = root.GetProperty("shadowRoutes")[0];
            Assert.Equal("orders.search", search.GetProperty("id").GetString());
            Assert.Equal("Legacy/LegacyOrdersSearch.cs", search.GetProperty("old").GetProperty("file").GetString());
            Assert.Equal(
                fixture.LineOf("Legacy/LegacyOrdersSearch.cs", "public sealed class LegacyOrdersSearch"),
                search.GetProperty("old").GetProperty("line").GetInt32());
            Assert.Equal(2, search.GetProperty("caseCount").GetInt32());
            var quote = root.GetProperty("shadowRoutes")[1];
            Assert.Equal("shipping.quote", quote.GetProperty("id").GetString());
            Assert.Equal(1, quote.GetProperty("caseCount").GetInt32());
            Assert.Equal("cases/shipping.quote.json", quote.GetProperty("cases").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void ShadowSuite_RejectsMissingHandlerLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var (suite, fixture) = GenerateSuite(f => f.SeedMissingHandlerTable());
            fixture.Dispose();
        });

        Assert.Contains("route 'orders.search'", error.Message);
        Assert.Contains("NoSuchService", error.Message);
        Assert.Contains("no class or record declaration", error.Message);
    }

    [Fact]
    public void ShadowSuite_RejectsAmbiguousHandlerLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var (suite, fixture) = GenerateSuite(f => { f.SeedAmbiguousNewHandler(); return f.ConfigPath; });
            fixture.Dispose();
        });

        Assert.Contains("'OrdersSearchService' is ambiguous", error.Message);
        Assert.Contains("declared 2 times", error.Message);
    }

    [Fact]
    public void ShadowSuite_RejectsMissingCasesFileLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var (suite, fixture) = GenerateSuite(f => f.SeedMissingCasesTable());
            fixture.Dispose();
        });

        Assert.Contains("route 'orders.search'", error.Message);
        Assert.Contains("cases file not found", error.Message);
        Assert.Contains("cases/missing.json", error.Message);
    }

    [Fact]
    public void Renderer_MarkdownAndJsonCarryEvidenceAndVerdicts()
    {
        var (report, config, fixture) = Analyze();
        try
        {
            var markdown = CockpitRenderer.Markdown(report, config);
            Assert.Contains("## Routes", markdown);
            Assert.Contains("`shipping.quote` | strangled", markdown);
            Assert.Contains("sunset window open", markdown);
            Assert.Contains("(`migration.json:", markdown);
            Assert.Contains("**warning** `migration/sunset-window` (route `shipping.quote`)", markdown);

            using var summary = JsonDocument.Parse(CockpitRenderer.Json(report, config));
            var root = summary.RootElement;
            Assert.Equal("sfg cockpit 0.5.0", root.GetProperty("generator").GetString());
            Assert.Equal(3, root.GetProperty("routes").GetArrayLength());
            var quote = root.GetProperty("routes").EnumerateArray()
                .Single(r => r.GetProperty("id").GetString() == "shipping.quote");
            Assert.True(quote.GetProperty("sunsetWindowOpen").GetBoolean());
            Assert.StartsWith("RouteTableSetup.cs:", quote.GetProperty("wiredAt").GetString());
            Assert.Equal(0, root.GetProperty("errors").GetInt32());
            Assert.Equal(1, root.GetProperty("warnings").GetInt32());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Renderer_DrawioIsWellFormedAndStateColored()
    {
        var (report, config, fixture) = Analyze();
        try
        {
            var xml = CockpitRenderer.Drawio(report);
            var document = XDocument.Parse(xml);
            Assert.Equal("mxfile", document.Root!.Name.LocalName);
            Assert.Equal("vibe", (string?)document.Root.Attribute("agent"));

            Assert.Contains("fillColor=#d5e8d4;strokeColor=#b85450;dashed=1", xml); // strangled + tripwire fired
            Assert.Contains("fillColor=#fff2cc", xml); // strangling
            Assert.Contains("fillColor=#f5f5f5", xml); // todo
            Assert.Contains("shipping.quote&lt;br/&gt;state: strangled", xml); // html label, xml-escaped
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Outputs_AreByteIdenticalOnRegeneration()
    {
        var fixture = new StranglerAppFixture();
        try
        {
            var config = MigrationConfig.Load(fixture.ConfigPath);
            var wired = PivotScanner.Scan(fixture.Root, config.RouteMethod);

            string RenderOnce() =>
                string.Concat(
                    CockpitRenderer.Markdown(new CockpitAnalyzer().Analyze(config, wired, "migration.json"), config),
                    CockpitRenderer.Json(new CockpitAnalyzer().Analyze(config, wired, "migration.json"), config),
                    CockpitRenderer.Drawio(new CockpitAnalyzer().Analyze(config, wired, "migration.json")));

            Assert.Equal(RenderOnce(), RenderOnce());

            var handlers = HandlerScanner.Scan(fixture.Root);
            var first = ShadowDiffGenerator.Generate(fixture.Root, Path.Combine(fixture.Root, "out-a"), config, handlers);
            var second = ShadowDiffGenerator.Generate(fixture.Root, Path.Combine(fixture.Root, "out-b"), config, handlers);
            Assert.Equal(
                first.Files.Select(f => (f.Path, f.Content)),
                second.Files.Select(f => (f.Path, f.Content)));
        }
        finally
        {
            fixture.Dispose();
        }
    }
}
