using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic cloud unit-economics estate (#83): two tenants (alpha, beta)
/// on two services (api, batch) in one month, with alpha's spend landing
/// exactly on its declared budget — the boundary is inclusive. Variants seed
/// the join failure modes (a dangling allocation, an unallocated cost, an
/// unbilled service, an unpriced tenant, over-allocation, an allocation gap,
/// a spend spike, an unbudgeted tenant, idle capacity) and the loader's
/// failure modes, so the tests can prove the extractor bites and never
/// passes silently. The estate is seeded demo data and says so.
/// </summary>
public sealed class UnitEconFixture : IDisposable
{
    public UnitEconFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-unitecon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "unit-econ.json");

        Write("billing.json", BaseBilling);
        Write("allocations.json", BaseAllocations);
        Write("telemetry.json", BaseTelemetry);
        Write("unit-econ.json", BaseConfig);
    }

    public string Root { get; }

    public string ConfigPath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    // ── the control: every join verified, every share sums to one, alpha's
    //    spend lands exactly on its budget (the boundary is inclusive).

    public const string BaseConfig = """
        {
          "name": "test-unit-economics",
          "seeded": true,
          "currency": "EUR",
          "granularity": "month",
          "billing": "billing.json",
          "allocations": "allocations.json",
          "telemetry": "telemetry.json",
          "budgets": {
            "alpha": 400,
            "beta": 150
          }
        }
        """;

    public const string BaseBilling = """
        {
          "export": "test-billing-1",
          "currency": "EUR",
          "lineItems": [
            { "service": "api", "period": "2026-09-01", "amount": 400.00 },
            { "service": "batch", "period": "2026-09-01", "amount": 100.00 }
          ]
        }
        """;

    public const string BaseAllocations = """
        {
          "manifest": "test-allocations-1",
          "rules": [
            { "tenant": "alpha", "service": "api", "share": 0.75 },
            { "tenant": "beta", "service": "api", "share": 0.25 },
            { "tenant": "alpha", "service": "batch", "share": 1.00 }
          ]
        }
        """;

    public const string BaseTelemetry = """
        {
          "source": "test-telemetry-1",
          "requests": [
            { "tenant": "alpha", "service": "api", "date": "2026-09-05", "requests": 1500 },
            { "tenant": "alpha", "service": "api", "date": "2026-09-20", "requests": 1500 },
            { "tenant": "beta", "service": "api", "date": "2026-09-05", "requests": 1000 },
            { "tenant": "alpha", "service": "batch", "date": "2026-09-05", "requests": 500 }
          ]
        }
        """;

    /// <summary>An allocation rule for a service the billing export never bills.</summary>
    public const string DanglingAllocations = """
        {
          "manifest": "test-allocations-1",
          "rules": [
            { "tenant": "alpha", "service": "api", "share": 0.75 },
            { "tenant": "beta", "service": "api", "share": 0.25 },
            { "tenant": "alpha", "service": "batch", "share": 1.00 },
            { "tenant": "alpha", "service": "search", "share": 0.10 }
          ]
        }
        """;

    /// <summary>No rule prices batch — its cost reaches no tenant; the
    /// telemetry variant below drops batch traffic so only the cost join
    /// bites.</summary>
    public const string UnallocatedAllocations = """
        {
          "manifest": "test-allocations-1",
          "rules": [
            { "tenant": "alpha", "service": "api", "share": 0.75 },
            { "tenant": "beta", "service": "api", "share": 0.25 }
          ]
        }
        """;

    public const string ApiOnlyTelemetry = """
        {
          "source": "test-telemetry-1",
          "requests": [
            { "tenant": "alpha", "service": "api", "date": "2026-09-05", "requests": 1500 },
            { "tenant": "alpha", "service": "api", "date": "2026-09-20", "requests": 1500 },
            { "tenant": "beta", "service": "api", "date": "2026-09-05", "requests": 1000 }
          ]
        }
        """;

    /// <summary>Telemetry serves requests on a service the billing export
    /// never bills.</summary>
    public const string UnbilledTelemetry = """
        {
          "source": "test-telemetry-1",
          "requests": [
            { "tenant": "alpha", "service": "api", "date": "2026-09-05", "requests": 1500 },
            { "tenant": "alpha", "service": "api", "date": "2026-09-20", "requests": 1500 },
            { "tenant": "beta", "service": "api", "date": "2026-09-05", "requests": 1000 },
            { "tenant": "alpha", "service": "batch", "date": "2026-09-05", "requests": 500 },
            { "tenant": "beta", "service": "search", "date": "2026-09-10", "requests": 400 }
          ]
        }
        """;

    /// <summary>Telemetry serves requests for a tenant no allocation rule
    /// prices.</summary>
    public const string UnpricedTelemetry = """
        {
          "source": "test-telemetry-1",
          "requests": [
            { "tenant": "alpha", "service": "api", "date": "2026-09-05", "requests": 1500 },
            { "tenant": "alpha", "service": "api", "date": "2026-09-20", "requests": 1500 },
            { "tenant": "beta", "service": "api", "date": "2026-09-05", "requests": 1000 },
            { "tenant": "alpha", "service": "batch", "date": "2026-09-05", "requests": 500 },
            { "tenant": "gamma", "service": "api", "date": "2026-09-12", "requests": 300 }
          ]
        }
        """;

    /// <summary>api's shares sum to 110% — the bill is double-counted; the
    /// inflated spend stays inside both budgets so only the share join
    /// bites.</summary>
    public const string OverAllocatedAllocations = """
        {
          "manifest": "test-allocations-1",
          "rules": [
            { "tenant": "alpha", "service": "api", "share": 0.75 },
            { "tenant": "beta", "service": "api", "share": 0.35 },
            { "tenant": "alpha", "service": "batch", "share": 1.00 }
          ]
        }
        """;

    /// <summary>api's shares sum to 95% — the unallocated remainder reaches
    /// no tenant and is published, not dropped.</summary>
    public const string GapAllocations = """
        {
          "manifest": "test-allocations-1",
          "rules": [
            { "tenant": "alpha", "service": "api", "share": 0.75 },
            { "tenant": "beta", "service": "api", "share": 0.20 },
            { "tenant": "alpha", "service": "batch", "share": 1.00 }
          ]
        }
        """;

    /// <summary>api's September line jumps 400 → 500: alpha spends 475
    /// (over its 400 budget) while beta spends 125 (inside its 150) — the
    /// spike flags one tenant with per-tenant evidence.</summary>
    public const string SpikedBilling = """
        {
          "export": "test-billing-1",
          "currency": "EUR",
          "lineItems": [
            { "service": "api", "period": "2026-09-01", "amount": 500.00 },
            { "service": "batch", "period": "2026-09-01", "amount": 100.00 }
          ]
        }
        """;

    /// <summary>api billed as two additive lines that bucket to the same
    /// month as the control's single line.</summary>
    public const string AdditiveBilling = """
        {
          "export": "test-billing-1",
          "currency": "EUR",
          "lineItems": [
            { "service": "api", "period": "2026-09-01", "amount": 250.00 },
            { "service": "api", "period": "2026-09-18", "amount": 150.00 },
            { "service": "batch", "period": "2026-09-01", "amount": 100.00 }
          ]
        }
        """;

    /// <summary>No budgets declared — spend cannot spike on anyone; the
    /// gap is published once per tenant, advisory.</summary>
    public const string UnbudgetedConfig = """
        {
          "name": "test-unit-economics",
          "seeded": true,
          "currency": "EUR",
          "granularity": "month",
          "billing": "billing.json",
          "allocations": "allocations.json",
          "telemetry": "telemetry.json"
        }
        """;

    /// <summary>A recorded (non-seeded) estate — the honesty line must say
    /// recorded exports, never SEEDED.</summary>
    public const string RecordedConfig = """
        {
          "name": "test-unit-economics",
          "seeded": false,
          "currency": "EUR",
          "granularity": "month",
          "billing": "billing.json",
          "allocations": "allocations.json",
          "telemetry": "telemetry.json",
          "budgets": {
            "alpha": 400,
            "beta": 150
          }
        }
        """;

    /// <summary>Telemetry without the batch rows — alpha is allocated batch
    /// cost but served no batch requests: idle capacity, published.</summary>
    public const string IdleTelemetry = """
        {
          "source": "test-telemetry-1",
          "requests": [
            { "tenant": "alpha", "service": "api", "date": "2026-09-05", "requests": 1500 },
            { "tenant": "alpha", "service": "api", "date": "2026-09-20", "requests": 1500 },
            { "tenant": "beta", "service": "api", "date": "2026-09-05", "requests": 1000 }
          ]
        }
        """;

    /// <summary>Week granularity: 2026-09-01 (Tue), 2026-09-02 (Wed), and
    /// 2026-09-03 (Thu) all bucket to the 2026-08-31 Monday.</summary>
    public const string WeekConfig = """
        {
          "name": "test-unit-economics",
          "seeded": true,
          "currency": "EUR",
          "granularity": "week",
          "billing": "billing.json",
          "allocations": "allocations.json",
          "telemetry": "telemetry.json",
          "budgets": {
            "alpha": 4000,
            "beta": 1500
          }
        }
        """;

    public const string WeekBilling = """
        {
          "export": "test-billing-1",
          "currency": "EUR",
          "lineItems": [
            { "service": "api", "period": "2026-09-01", "amount": 400.00 },
            { "service": "batch", "period": "2026-09-03", "amount": 100.00 }
          ]
        }
        """;

    public const string WeekTelemetry = """
        {
          "source": "test-telemetry-1",
          "requests": [
            { "tenant": "alpha", "service": "api", "date": "2026-09-01", "requests": 3000 },
            { "tenant": "alpha", "service": "batch", "date": "2026-09-03", "requests": 500 },
            { "tenant": "beta", "service": "api", "date": "2026-09-02", "requests": 1000 }
          ]
        }
        """;
}
