using System.Text.Json;
using Sfg.Toolkit.UnitEcon;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// Cloud unit-economics extractor (#83): the control stays silent, every
/// unitecon/ rule bites with file:line evidence, the budget is inclusive at
/// the boundary, every loader failure mode is loud, the SEEDED honesty line
/// is always on the board, and the boards are deterministic. Recorded exports
/// only — the harness never calls a live cloud API.
/// </summary>
public sealed class UnitEconTests
{
    private static UnitEconReport Analyze(Action<UnitEconFixture>? seed = null, string configName = "unit-econ.json")
    {
        var fixture = new UnitEconFixture();
        try
        {
            seed?.Invoke(fixture);
            var config = UnitEconConfig.Load(Path.Combine(fixture.Root, configName));
            var billing = UnitEconFiles.LoadBilling(config.BillingPath, config.Currency);
            var allocations = UnitEconFiles.LoadAllocations(config.AllocationsPath);
            var telemetry = UnitEconFiles.LoadTelemetry(config.TelemetryPath);
            return UnitEconAnalyzer.Analyze(
                config,
                billing,
                allocations,
                telemetry,
                config.BillingPath,
                config.AllocationsPath,
                config.TelemetryPath);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static UnitEconReport AnalyzeWith(Action<UnitEconFixture> seed, string configName = "unit-econ.json") =>
        Analyze(seed, configName);

    private static UnitEconFinding Finding(UnitEconReport report, string rule) =>
        Assert.Single(report.Findings, f => f.Rule == rule);

    private static void AllEvidenceAnchored(UnitEconFinding finding)
    {
        Assert.NotEmpty(finding.Evidence);
        Assert.All(finding.Evidence, anchor => Assert.Matches(@".+:[0-9]+", anchor));
    }

    private static (string Markdown, string Json) Render(Action<UnitEconFixture>? seed = null, string configName = "unit-econ.json")
    {
        var fixture = new UnitEconFixture();
        try
        {
            seed?.Invoke(fixture);
            var config = UnitEconConfig.Load(Path.Combine(fixture.Root, configName));
            var billing = UnitEconFiles.LoadBilling(config.BillingPath, config.Currency);
            var allocations = UnitEconFiles.LoadAllocations(config.AllocationsPath);
            var telemetry = UnitEconFiles.LoadTelemetry(config.TelemetryPath);
            var report = UnitEconAnalyzer.Analyze(
                config,
                billing,
                allocations,
                telemetry,
                config.BillingPath,
                config.AllocationsPath,
                config.TelemetryPath);
            return (UnitEconRenderer.Markdown(config, report), UnitEconRenderer.Json(config, report));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    // ── the control stays silent, and alpha's spend sits exactly on its
    //    budget: the boundary is inclusive.

    [Fact]
    public void ControlStaysSilent()
    {
        var report = Analyze();
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Warnings);
        Assert.All(report.Spend, row => Assert.Equal("inside budget", row.Status));
    }

    [Fact]
    public void ControlCountsTheEstate()
    {
        var report = Analyze();
        Assert.Equal(2, report.Tenants);
        Assert.Equal(2, report.Services);
        Assert.Equal(1, report.Periods);
        Assert.Equal(2, report.Spend.Count);
        Assert.Equal(3, report.CostPerRequest.Count);
    }

    [Fact]
    public void TrendRowsPriceRequests()
    {
        var report = Analyze();
        var alphaApi = Assert.Single(report.CostPerRequest, row => row.Tenant == "alpha" && row.Service == "api");
        Assert.Equal(300.00, alphaApi.AllocatedCost);
        Assert.Equal(3000, alphaApi.Requests);
        Assert.Equal(0.1, alphaApi.CostPerRequest);
        var alphaBatch = Assert.Single(report.CostPerRequest, row => row.Tenant == "alpha" && row.Service == "batch");
        Assert.Equal(100.00, alphaBatch.AllocatedCost);
        Assert.Equal(0.2, alphaBatch.CostPerRequest);
    }

    [Fact]
    public void AdditiveBillingLinesBucketToOnePeriod()
    {
        var report = AnalyzeWith(f => f.Write("billing.json", UnitEconFixture.AdditiveBilling));
        Assert.Empty(report.Findings);
        var alpha = Assert.Single(report.Spend, row => row.Tenant == "alpha");
        Assert.Equal(400.00, alpha.Spend);
        Assert.Equal("2026-09", alpha.Period);
    }

    [Fact]
    public void WeekGranularityBucketsByIsoMonday()
    {
        var report = AnalyzeWith(f =>
        {
            f.Write("unit-econ.json", UnitEconFixture.WeekConfig);
            f.Write("billing.json", UnitEconFixture.WeekBilling);
            f.Write("telemetry.json", UnitEconFixture.WeekTelemetry);
        });
        Assert.Empty(report.Findings);
        Assert.Equal(1, report.Periods);
        Assert.All(report.Spend, row => Assert.Equal("2026-08-31", row.Period));
        Assert.All(report.CostPerRequest, row => Assert.Equal("2026-08-31", row.Period));
    }

    // ── the loader is loud

    [Fact]
    public void MissingConfigIsLoud()
    {
        Assert.Throws<FileNotFoundException>(() => UnitEconConfig.Load("/nowhere/unit-econ.json"));
    }

    [Fact]
    public void InvalidConfigJsonIsLoud()
    {
        using var fixture = new UnitEconFixture();
        fixture.Write("broken-config.json", "{ not json");
        Assert.Throws<InvalidOperationException>(() => UnitEconConfig.Load(Path.Combine(fixture.Root, "broken-config.json")));
    }

    [Fact]
    public void ConfigWithoutNameIsLoud()
    {
        using var fixture = new UnitEconFixture();
        fixture.Write("no-name.json", """
            {
              "seeded": true,
              "currency": "EUR",
              "billing": "billing.json",
              "allocations": "allocations.json",
              "telemetry": "telemetry.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => UnitEconConfig.Load(Path.Combine(fixture.Root, "no-name.json")));
    }

    [Fact]
    public void ConfigWithoutSeededFlagIsLoud()
    {
        // The honesty is explicit, never guessed: a demo estate must say it
        // is seeded, a real one must say it is not.
        using var fixture = new UnitEconFixture();
        fixture.Write("no-seeded.json", """
            {
              "name": "test-unit-economics",
              "currency": "EUR",
              "billing": "billing.json",
              "allocations": "allocations.json",
              "telemetry": "telemetry.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => UnitEconConfig.Load(Path.Combine(fixture.Root, "no-seeded.json")));
    }

    [Fact]
    public void ConfigWithoutCurrencyIsLoud()
    {
        using var fixture = new UnitEconFixture();
        fixture.Write("no-currency.json", """
            {
              "name": "test-unit-economics",
              "seeded": true,
              "billing": "billing.json",
              "allocations": "allocations.json",
              "telemetry": "telemetry.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => UnitEconConfig.Load(Path.Combine(fixture.Root, "no-currency.json")));
    }

    [Fact]
    public void InvalidGranularityIsLoud()
    {
        using var fixture = new UnitEconFixture();
        fixture.Write("quarter-config.json", """
            {
              "name": "test-unit-economics",
              "seeded": true,
              "currency": "EUR",
              "granularity": "quarter",
              "billing": "billing.json",
              "allocations": "allocations.json",
              "telemetry": "telemetry.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => UnitEconConfig.Load(Path.Combine(fixture.Root, "quarter-config.json")));
    }

    [Fact]
    public void MissingBillingFileIsLoud()
    {
        Assert.Throws<FileNotFoundException>(() => AnalyzeWith(f => f.Write("unit-econ.json", """
            {
              "name": "test-unit-economics",
              "seeded": true,
              "currency": "EUR",
              "billing": "nowhere.json",
              "allocations": "allocations.json",
              "telemetry": "telemetry.json"
            }
            """)));
    }

    [Fact]
    public void BillingCurrencyMismatchIsLoud()
    {
        // A unit-economics join must not cross currencies silently.
        Assert.Throws<InvalidOperationException>(() => AnalyzeWith(f => f.Write("billing.json", """
            {
              "export": "test-billing-1",
              "currency": "USD",
              "lineItems": [
                { "service": "api", "period": "2026-09-01", "amount": 400.00 }
              ]
            }
            """)));
    }

    [Fact]
    public void BillingWithoutLineItemsIsLoud()
    {
        Assert.Throws<InvalidOperationException>(() => AnalyzeWith(f => f.Write("billing.json", """
            {
              "export": "test-billing-1",
              "currency": "EUR",
              "lineItems": []
            }
            """)));
    }

    [Fact]
    public void NonPositiveBillingAmountIsLoud()
    {
        Assert.Throws<InvalidOperationException>(() => AnalyzeWith(f => f.Write("billing.json", """
            {
              "export": "test-billing-1",
              "currency": "EUR",
              "lineItems": [
                { "service": "api", "period": "2026-09-01", "amount": 0 }
              ]
            }
            """)));
    }

    [Fact]
    public void DuplicateAllocationRuleIsLoud()
    {
        Assert.Throws<InvalidOperationException>(() => AnalyzeWith(f => f.Write("allocations.json", """
            {
              "manifest": "test-allocations-1",
              "rules": [
                { "tenant": "alpha", "service": "api", "share": 0.75 },
                { "tenant": "alpha", "service": "api", "share": 0.25 },
                { "tenant": "alpha", "service": "batch", "share": 1.00 }
              ]
            }
            """)));
    }

    [Fact]
    public void ShareOutOfRangeIsLoud()
    {
        Assert.Throws<InvalidOperationException>(() => AnalyzeWith(f => f.Write("allocations.json", """
            {
              "manifest": "test-allocations-1",
              "rules": [
                { "tenant": "alpha", "service": "api", "share": 1.50 },
                { "tenant": "alpha", "service": "batch", "share": 1.00 }
              ]
            }
            """)));
    }

    [Fact]
    public void NonPositiveTelemetryCountIsLoud()
    {
        Assert.Throws<InvalidOperationException>(() => AnalyzeWith(f => f.Write("telemetry.json", """
            {
              "source": "test-telemetry-1",
              "requests": [
                { "tenant": "alpha", "service": "api", "date": "2026-09-05", "requests": 0 }
              ]
            }
            """)));
    }

    // ── every join bites, in both directions

    [Fact]
    public void DanglingAllocationBites()
    {
        var report = AnalyzeWith(f => f.Write("allocations.json", UnitEconFixture.DanglingAllocations));
        var finding = Finding(report, "unitecon/dangling-allocation");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("allocation without a cost is a dangling join", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(1, report.Errors);
    }

    [Fact]
    public void UnallocatedCostBites()
    {
        var report = AnalyzeWith(f =>
        {
            f.Write("allocations.json", UnitEconFixture.UnallocatedAllocations);
            f.Write("telemetry.json", UnitEconFixture.ApiOnlyTelemetry);
        });
        var finding = Finding(report, "unitecon/unallocated-cost");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'batch'", finding.Message);
        Assert.Contains("reaches no tenant", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(1, report.Errors);
    }

    [Fact]
    public void UnbilledServiceBites()
    {
        var report = AnalyzeWith(f => f.Write("telemetry.json", UnitEconFixture.UnbilledTelemetry));
        var finding = Finding(report, "unitecon/unbilled-service");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'search'", finding.Message);
        Assert.Contains("served but never billed", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(1, report.Errors);
    }

    [Fact]
    public void UnpricedTenantBites()
    {
        var report = AnalyzeWith(f => f.Write("telemetry.json", UnitEconFixture.UnpricedTelemetry));
        var finding = Finding(report, "unitecon/unpriced-tenant");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'gamma'", finding.Message);
        Assert.Contains("requests without a price", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(1, report.Errors);
    }

    [Fact]
    public void OverAllocationBites()
    {
        var report = AnalyzeWith(f => f.Write("allocations.json", UnitEconFixture.OverAllocatedAllocations));
        var finding = Finding(report, "unitecon/over-allocation");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("110.0%", finding.Message);
        Assert.Contains("double-count the bill", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(1, report.Errors);
    }

    [Fact]
    public void AllocationGapIsAdvisory()
    {
        var report = AnalyzeWith(f => f.Write("allocations.json", UnitEconFixture.GapAllocations));
        var finding = Finding(report, "unitecon/allocation-gap");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("95.0%", finding.Message);
        Assert.Contains("published, not dropped", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(0, report.Errors);
        Assert.Equal(1, report.Warnings);
    }

    [Fact]
    public void SpendSpikeBitesWithPerTenantEvidence()
    {
        var report = AnalyzeWith(f => f.Write("billing.json", UnitEconFixture.SpikedBilling));
        var finding = Finding(report, "unitecon/spend-spike");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("tenant 'alpha' spent EUR 475.00 in 2026-09 — over its EUR 400.00 budget", finding.Message);
        AllEvidenceAnchored(finding);
        Assert.Equal(2, finding.Evidence.Count);
        // Per-tenant evidence: beta's spike is not alpha's — only alpha is over.
        var beta = Assert.Single(report.Spend, row => row.Tenant == "beta");
        Assert.Equal(125.00, beta.Spend);
        Assert.Equal("inside budget", beta.Status);
        Assert.Equal(1, report.Errors);
    }

    [Fact]
    public void SpendAtTheBudgetBoundaryIsInside()
    {
        // The control's alpha spends exactly 400.00 against a 400 budget —
        // budgets are inclusive at the boundary, never a spike.
        var report = Analyze();
        var alpha = Assert.Single(report.Spend, row => row.Tenant == "alpha");
        Assert.Equal(400.00, alpha.Spend);
        Assert.Equal(400.00, alpha.Budget);
        Assert.Equal("inside budget", alpha.Status);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "unitecon/spend-spike");
    }

    [Fact]
    public void UnbudgetedTenantIsAdvisory()
    {
        var report = AnalyzeWith(f => f.Write("unit-econ.json", UnitEconFixture.UnbudgetedConfig));
        // Fires exactly once per tenant, never once per period.
        Assert.Equal(2, report.Findings.Count(f => f.Rule == "unitecon/unbudgeted-tenant"));
        var alpha = Assert.Single(report.Findings, f => f.Rule == "unitecon/unbudgeted-tenant" && f.Message.Contains("'alpha'"));
        Assert.Equal("warning", alpha.Severity);
        AllEvidenceAnchored(alpha);
        Assert.Equal(0, report.Errors);
        Assert.Equal(2, report.Warnings);
        Assert.All(report.Spend, row => Assert.Equal("unbudgeted", row.Status));
    }

    [Fact]
    public void CostWithoutRequestsIsAdvisory()
    {
        var report = AnalyzeWith(f => f.Write("telemetry.json", UnitEconFixture.IdleTelemetry));
        var finding = Finding(report, "unitecon/cost-without-requests");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("'batch'", finding.Message);
        Assert.Contains("idle capacity is published, not hidden", finding.Message);
        AllEvidenceAnchored(finding);
        // The idle pair still trends — with an unpriced null cell, not a gap.
        var idle = Assert.Single(report.CostPerRequest, row => row.Tenant == "alpha" && row.Service == "batch");
        Assert.Equal(100.00, idle.AllocatedCost);
        Assert.Equal(0, idle.Requests);
        Assert.Null(idle.CostPerRequest);
        Assert.Equal(0, report.Errors);
        Assert.Equal(1, report.Warnings);
    }

    [Fact]
    public void FindingsAreCappedWithAMoreLine()
    {
        var (markdown, _) = Render(f =>
        {
            f.Write("unit-econ.json", """
                {
                  "name": "test-unit-economics",
                  "seeded": true,
                  "currency": "EUR",
                  "granularity": "month",
                  "billing": "billing.json",
                  "allocations": "allocations.json",
                  "telemetry": "telemetry.json",
                  "maxFindings": 1
                }
                """);
        }, configName: "unit-econ.json");
        // No budgets declared: both tenants are unbudgeted — capped at 1.
        Assert.Contains("…and 1 more", markdown);
        Assert.Contains("raise `maxFindings` beyond 1", markdown);
    }

    [Fact]
    public void EveryFindingCitesLineAnchors()
    {
        // Every evidence anchor of every finding of a seeded run is file:line.
        var seeded = AnalyzeWith(f =>
        {
            f.Write("allocations.json", UnitEconFixture.DanglingAllocations);
            f.Write("telemetry.json", UnitEconFixture.UnpricedTelemetry);
        });
        Assert.Equal(2, seeded.Findings.Count);
        Assert.All(seeded.Findings, f => Assert.All(f.Evidence, anchor => Assert.Matches(@".+:[0-9]+", anchor)));
    }

    [Fact]
    public void FindingsAreSeverityOrderedErrorsFirst()
    {
        var seeded = AnalyzeWith(f =>
        {
            f.Write("allocations.json", UnitEconFixture.GapAllocations);
            f.Write("telemetry.json", UnitEconFixture.UnpricedTelemetry);
        });
        Assert.Equal("error", seeded.Findings[0].Severity);
        Assert.Equal("warning", seeded.Findings[1].Severity);
        Assert.Equal("unitecon/unpriced-tenant", seeded.Findings[0].Rule);
        Assert.Equal("unitecon/allocation-gap", seeded.Findings[1].Rule);
    }

    // ── the boards

    [Fact]
    public void BoardsAreDeterministic()
    {
        var first = Render();
        var second = Render();
        Assert.Equal(first.Markdown, second.Markdown);
        Assert.Equal(first.Json, second.Json);
    }

    [Fact]
    public void SeededEstateIsLabeledOnEveryBoard()
    {
        // The honesty is explicit: a seeded estate says SEEDED on the board.
        var (markdown, json) = Render();
        Assert.Contains("SEEDED estate — no real billing history backs these numbers", markdown);
        Assert.Contains("seeded", json);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("seeded").GetBoolean());
    }

    [Fact]
    public void RecordedEstateIsLabeledToo()
    {
        var (markdown, json) = Render(f => f.Write("unit-econ.json", UnitEconFixture.RecordedConfig));
        Assert.Contains("Recorded exports only", markdown);
        Assert.DoesNotContain("SEEDED", markdown);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("seeded").GetBoolean());
    }

    [Fact]
    public void MarkdownCarriesTheSummaryLine()
    {
        var (markdown, _) = Render();
        Assert.Contains("sfg unit-econ 0.12.0", markdown);
        Assert.Contains("Summary: 2 tenant(s), 2 service(s), 1 period(s), 2 spend row(s), 3 cost-per-request row(s), 0 finding(s) — 0 error(s), 0 warning(s).", markdown);
        Assert.Contains("Clean run: every billed service is allocated, every served request is priced, every join verified, and every tenant is inside its budget.", markdown);
    }

    [Fact]
    public void JsonCarriesGeneratorAndCounts()
    {
        var (_, json) = Render();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("sfg unit-econ 0.12.0", document.RootElement.GetProperty("generator").GetString());
        Assert.Equal("test-unit-economics", document.RootElement.GetProperty("estate").GetString());
        Assert.Equal("EUR", document.RootElement.GetProperty("currency").GetString());
        Assert.Equal("month", document.RootElement.GetProperty("granularity").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("spend").GetArrayLength());
        Assert.Equal(3, document.RootElement.GetProperty("costPerRequest").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("errors").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("warnings").GetInt32());
    }
}
