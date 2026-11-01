namespace Sfg.Toolkit.UnitEcon;

/// <summary>
/// Runs the cloud unit-economics extractor (#83): joins a recorded billing
/// export to per-tenant allocation shares and recorded request telemetry to
/// trend cost-per-request per tenant and service, and verifies every join in
/// both directions (the #68 discipline) — an allocation rule must point at a
/// billed service, a billed service must reach at least one tenant, a served
/// service must be billed, a served tenant must be priced by an allocation
/// rule, and a service's shares must sum to one. Budget thresholds turn a
/// spend spike into a per-tenant finding with evidence. It reads recorded
/// exports only — never a live cloud API. Trends, never scores: tenants are
/// not ranked, their unit costs are published.
/// </summary>
public static class UnitEconAnalyzer
{
    private const double ShareTolerance = 1e-9;

    public static UnitEconReport Analyze(
        UnitEconConfig config,
        BillingExport billing,
        AllocationManifest allocations,
        TelemetryExport telemetry,
        string? billingPathUsed = null,
        string? allocationsPathUsed = null,
        string? telemetryPathUsed = null)
    {
        var findings = new List<UnitEconFinding>();
        var billingName = Path.GetFileName(billingPathUsed ?? config.BillingPath);
        var allocationsName = Path.GetFileName(allocationsPathUsed ?? config.AllocationsPath);
        var telemetryName = Path.GetFileName(telemetryPathUsed ?? config.TelemetryPath);

        string PeriodOf(DateTimeOffset timestamp) => config.Granularity switch
        {
            "day" => timestamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            "week" => IsoWeekMonday(timestamp),
            _ => timestamp.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        };

        // ── billing buckets: cost per service per period ──────────────────
        var billedServices = new SortedSet<string>(StringComparer.Ordinal);
        var costByServicePeriod = new Dictionary<(string Service, string Period), double>();
        var firstLineByService = new Dictionary<string, BillingLine>(StringComparer.Ordinal);
        foreach (var line in billing.Lines)
        {
            billedServices.Add(line.Service);
            var key = (line.Service, PeriodOf(line.Period));
            costByServicePeriod[key] = costByServicePeriod.GetValueOrDefault(key) + line.Amount;
            if (!firstLineByService.TryGetValue(line.Service, out var first) || line.Line < first.Line)
            {
                firstLineByService[line.Service] = line;
            }
        }

        // ── join: allocation rule → billed service (dangling allocation),
        //    and billed service → allocation rule (unallocated cost) ────────
        var rulesByService = new Dictionary<string, List<AllocationRule>>(StringComparer.Ordinal);
        foreach (var rule in allocations.Rules)
        {
            if (!billedServices.Contains(rule.Service))
            {
                findings.Add(new UnitEconFinding(
                    "unitecon/dangling-allocation",
                    "error",
                    $"allocation rule prices '{rule.Service}' for '{rule.Tenant}' but the billing export has no line for it — an allocation without a cost is a dangling join",
                    new[] { rule.Anchor(allocationsName) }));
                continue;
            }

            if (!rulesByService.TryGetValue(rule.Service, out var list))
            {
                rulesByService[rule.Service] = list = new List<AllocationRule>();
            }

            list.Add(rule);
        }

        foreach (var service in billedServices)
        {
            if (!rulesByService.TryGetValue(service, out var rules) || rules.Count == 0)
            {
                findings.Add(new UnitEconFinding(
                    "unitecon/unallocated-cost",
                    "error",
                    $"billed service '{service}' has no allocation rules — its cost reaches no tenant",
                    new[] { firstLineByService[service].Anchor(billingName) }));
                continue;
            }

            // ── join: a service's shares must sum to one — under-allocation
            //    leaves cost unhomed (advisory), over-allocation double-counts
            var sum = rules.Sum(r => r.Share);
            var anchors = rules
                .OrderBy(r => r.Line)
                .Select(r => r.Anchor(allocationsName))
                .ToArray();
            if (sum > 1 + ShareTolerance)
            {
                findings.Add(new UnitEconFinding(
                    "unitecon/over-allocation",
                    "error",
                    $"billed service '{service}' allocates {Pct(sum)} of its cost across tenants — shares summing over 100% double-count the bill",
                    anchors));
            }
            else if (sum < 1 - ShareTolerance)
            {
                findings.Add(new UnitEconFinding(
                    "unitecon/allocation-gap",
                    "warning",
                    $"billed service '{service}' allocates only {Pct(sum)} of its cost — the unallocated remainder reaches no tenant and is published, not dropped",
                    anchors));
            }
        }

        // ── join: telemetry → billing (unbilled service), telemetry →
        //    allocation (unpriced tenant), in both directions of the pair ──
        var requestsByPairPeriod = new Dictionary<(string Tenant, string Service, string Period), long>();
        var firstRowByService = new Dictionary<string, TelemetryRow>(StringComparer.Ordinal);
        var firstRowByPair = new Dictionary<(string Tenant, string Service), TelemetryRow>();
        foreach (var row in telemetry.Rows)
        {
            if (!billedServices.Contains(row.Service))
            {
                if (!firstRowByService.TryGetValue(row.Service, out var first) || row.Line < first.Line)
                {
                    firstRowByService[row.Service] = row;
                }

                continue;
            }

            var key = (row.Tenant, row.Service, PeriodOf(row.Date));
            requestsByPairPeriod[key] = requestsByPairPeriod.GetValueOrDefault(key) + row.Requests;
            var pair = (row.Tenant, row.Service);
            if (!firstRowByPair.TryGetValue(pair, out var firstPair) || row.Line < firstPair.Line)
            {
                firstRowByPair[pair] = row;
            }
        }

        foreach (var service in firstRowByService.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            findings.Add(new UnitEconFinding(
                "unitecon/unbilled-service",
                "error",
                $"telemetry serves requests on '{service}' but the billing export has no line for it — served but never billed, the unit cost is unknowable",
                new[] { firstRowByService[service].Anchor(telemetryName) }));
        }

        var pricedPairs = new HashSet<(string Tenant, string Service)>();
        foreach (var rule in allocations.Rules)
        {
            if (billedServices.Contains(rule.Service))
            {
                pricedPairs.Add((rule.Tenant, rule.Service));
            }
        }

        foreach (var pair in firstRowByPair.Keys.OrderBy(p => p.Tenant, StringComparer.Ordinal).ThenBy(p => p.Service, StringComparer.Ordinal))
        {
            if (pricedPairs.Contains(pair))
            {
                continue;
            }

            findings.Add(new UnitEconFinding(
                "unitecon/unpriced-tenant",
                "error",
                $"tenant '{pair.Tenant}' serves requests on '{pair.Service}' but no allocation rule prices it — requests without a price",
                new[] { firstRowByPair[pair].Anchor(telemetryName) }));
        }

        // ── allocated cost per tenant/service/period, and the trends ──────
        var tenants = new SortedSet<string>(StringComparer.Ordinal);
        var periods = new SortedSet<string>(StringComparer.Ordinal);
        var allocatedByPairPeriod = new Dictionary<(string Tenant, string Service, string Period), double>();
        var firstRuleByTenantService = new Dictionary<(string Tenant, string Service), AllocationRule>();
        var firstRuleByTenant = new Dictionary<string, AllocationRule>(StringComparer.Ordinal);
        foreach (var service in rulesByService.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            var billedPeriods = costByServicePeriod
                .Where(pair => pair.Key.Service == service)
                .Select(pair => pair.Key.Period)
                .Distinct(StringComparer.Ordinal);
            foreach (var period in billedPeriods)
            {
                var cost = costByServicePeriod[(service, period)];
                foreach (var rule in rulesByService[service])
                {
                    allocatedByPairPeriod[(rule.Tenant, service, period)] = cost * rule.Share;
                    tenants.Add(rule.Tenant);
                    periods.Add(period);
                    if (!firstRuleByTenantService.TryGetValue((rule.Tenant, service), out var pairRule) || rule.Line < pairRule.Line)
                    {
                        firstRuleByTenantService[(rule.Tenant, service)] = rule;
                    }

                    if (!firstRuleByTenant.TryGetValue(rule.Tenant, out var tenantRule) || rule.Line < tenantRule.Line)
                    {
                        firstRuleByTenant[rule.Tenant] = rule;
                    }
                }
            }
        }

        // cost without requests: allocated spend on a pair that served nothing
        // — idle capacity is published, not hidden (advisory by design).
        foreach (var key in allocatedByPairPeriod.Keys.OrderBy(k => k.Tenant, StringComparer.Ordinal)
                     .ThenBy(k => k.Service, StringComparer.Ordinal).ThenBy(k => k.Period, StringComparer.Ordinal))
        {
            if (requestsByPairPeriod.TryGetValue(key, out var requests) && requests > 0)
            {
                continue;
            }

            var rule = firstRuleByTenantService[(key.Tenant, key.Service)];
            findings.Add(new UnitEconFinding(
                "unitecon/cost-without-requests",
                "warning",
                $"tenant '{key.Tenant}' was allocated {config.Currency} {Money(allocatedByPairPeriod[key])} on '{key.Service}' in {key.Period} but served no requests — idle capacity is published, not hidden",
                new[] { rule.Anchor(allocationsName) }));
        }

        // ── cost-per-request trends: per tenant, service, period ───────────
        var costPerRequestRows = new List<CostPerRequestRow>();
        foreach (var key in allocatedByPairPeriod.Keys.OrderBy(k => k.Tenant, StringComparer.Ordinal)
                     .ThenBy(k => k.Service, StringComparer.Ordinal).ThenBy(k => k.Period, StringComparer.Ordinal))
        {
            var requests = requestsByPairPeriod.GetValueOrDefault(key);
            var allocated = Round2(allocatedByPairPeriod[key]);
            var unit = requests > 0 ? Math.Round(allocated / requests, 4, MidpointRounding.AwayFromZero) : (double?)null;
            costPerRequestRows.Add(new CostPerRequestRow(key.Tenant, key.Service, key.Period, allocated, requests, unit));
        }

        // ── spend per tenant per period against the declared budgets ──────
        var spendRows = new List<SpendRow>();
        var spendByTenantPeriod = new Dictionary<(string Tenant, string Period), double>();
        foreach (var key in allocatedByPairPeriod.Keys)
        {
            spendByTenantPeriod[(key.Tenant, key.Period)] =
                spendByTenantPeriod.GetValueOrDefault((key.Tenant, key.Period)) + allocatedByPairPeriod[key];
        }

        foreach (var key in spendByTenantPeriod.Keys.OrderBy(k => k.Tenant, StringComparer.Ordinal)
                     .ThenBy(k => k.Period, StringComparer.Ordinal))
        {
            var spend = Round2(spendByTenantPeriod[key]);
            if (!config.Budgets.TryGetValue(key.Tenant, out var budget))
            {
                spendRows.Add(new SpendRow(key.Tenant, key.Period, spend, null, "unbudgeted"));
                continue;
            }

            // Budgets are inclusive at the boundary: spending exactly the
            // budget is on-budget, never a spike.
            var overBudget = spend > budget;
            if (overBudget)
            {
                findings.Add(new UnitEconFinding(
                    "unitecon/spend-spike",
                    "error",
                    $"tenant '{key.Tenant}' spent {config.Currency} {Money(spend)} in {key.Period} — over its {config.Currency} {Money(budget)} budget",
                    new[]
                    {
                        config.Anchor($"\"{key.Tenant}\""),
                        firstRuleByTenant[key.Tenant].Anchor(allocationsName),
                    }));
            }

            spendRows.Add(new SpendRow(key.Tenant, key.Period, spend, budget, overBudget ? "over budget" : "inside budget"));
        }

        // a tenant with allocated spend but no declared budget cannot spike —
        // the gap is published so it can be closed, not passed silently.
        foreach (var tenant in spendByTenantPeriod.Keys.Select(k => k.Tenant).Distinct(StringComparer.Ordinal)
                     .OrderBy(t => t, StringComparer.Ordinal))
        {
            if (!config.Budgets.ContainsKey(tenant))
            {
                findings.Add(new UnitEconFinding(
                    "unitecon/unbudgeted-tenant",
                    "warning",
                    $"tenant '{tenant}' has allocated spend but no declared budget — a spend spike on it cannot be caught",
                    new[] { firstRuleByTenant[tenant].Anchor(allocationsName) }));
            }
        }

        var ordered = findings
            .Distinct()
            .OrderBy(f => f, UnitEconFindingComparer.Instance)
            .ToArray();
        var rendered = ordered.Take(config.MaxFindings).ToArray();

        return new UnitEconReport
        {
            Estate = config.Name,
            Export = billing.Export,
            Manifest = allocations.Manifest,
            Source = telemetry.Source,
            Tenants = tenants.Count,
            Services = rulesByService.Keys.Count,
            Periods = periods.Count,
            Spend = spendRows.ToArray(),
            CostPerRequest = costPerRequestRows.ToArray(),
            Findings = ordered,
            RenderedFindings = rendered,
            Errors = ordered.Count(f => f.Severity == "error"),
            Warnings = ordered.Count(f => f.Severity == "warning"),
            Cap = config.MaxFindings,
        };
    }

    /// <summary>ISO-week key: the Monday of the week the timestamp falls in.</summary>
    private static string IsoWeekMonday(DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime;
        var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysFromMonday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    internal static string Money(double value) =>
        value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static string Pct(double share) =>
        $"{Math.Round(share * 100.0, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%";

    private sealed class UnitEconFindingComparer : IComparer<UnitEconFinding>
    {
        public static readonly UnitEconFindingComparer Instance = new();

        public int Compare(UnitEconFinding? x, UnitEconFinding? y) => (x ?? throw new ArgumentNullException(nameof(x)))
            .CompareTo(y ?? throw new ArgumentNullException(nameof(y)));
    }
}
