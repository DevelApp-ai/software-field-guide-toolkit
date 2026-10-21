namespace Sfg.Toolkit.Cockpit;

public sealed record CockpitFinding(
    string Rule,
    string RouteId,
    string Severity,
    string Message,
    string Evidence);

/// <summary>
/// One route as the cockpit sees it after analysis: declared state, wiring
/// status, traffic history, and the sunset verdict. No guesses — a route
/// with no traffic history simply reports no verdict.
/// </summary>
public sealed record CockpitRouteView(
    string Id,
    string State,
    bool Wired,
    bool Shadow,
    IReadOnlyList<long> Traffic,
    decimal Threshold,
    int SunsetPeriods,
    bool SunsetWindowOpen,
    string Evidence);

public sealed record CockpitReport(
    IReadOnlyList<CockpitRouteView> Routes,
    IReadOnlyList<CockpitFinding> Findings)
{
    public int Errors => Findings.Count(f => f.Severity == "error");

    public int Warnings => Findings.Count(f => f.Severity == "warning");
}

/// <summary>
/// The strangler-fig cockpit analyzer (#56). Three questions, answered with
/// file:line evidence and never a guess:
/// routing-seam coverage (is every declared route actually wired?), the
/// sunset tripwire (has a strangled route's old-implementation traffic stayed
/// under the retirement threshold for the whole sunset window — or is the
/// fig not finished strangling?), and shadow-diff wiring (declared shadow
/// routes get a generated diff suite elsewhere). Findings sort errors first,
/// then by rule, then by route id.
/// </summary>
public sealed class CockpitAnalyzer
{
    public CockpitReport Analyze(MigrationConfig config, IReadOnlyList<WiredRoute> wired, string tableFile)
    {
        var routes = new List<CockpitRouteView>();
        var findings = new List<CockpitFinding>();

        foreach (var route in config.Routes)
        {
            var wiredSite = wired.FirstOrDefault(w => string.Equals(w.Id, route.Id, StringComparison.Ordinal));
            var threshold = route.RetirementThreshold ?? config.DefaultRetirementThreshold;
            var periods = route.SunsetPeriods ?? config.DefaultSunsetPeriods;
            var sunsetWindowOpen = false;

            if (wiredSite is null)
            {
                findings.Add(new CockpitFinding(
                    "migration/unwired-route",
                    route.Id,
                    "error",
                    $"route '{route.Id}' is declared {route.State} but never wired at the routing seam — " +
                    "the routing seam (the pivot) must call " + config.RouteMethod + $"(\"{route.Id}\", ...) " +
                    "before the migration can claim it",
                    $"{tableFile}:{route.Line}"));
            }

            // The sunset tripwire only speaks about strangled routes, and only
            // once the traffic history covers the full sunset window.
            if (route.State == "strangled" && route.Traffic.Count >= periods)
            {
                var window = route.Traffic.Skip(route.Traffic.Count - periods).ToList();
                if (window.All(t => t <= threshold))
                {
                    sunsetWindowOpen = true;
                    findings.Add(new CockpitFinding(
                        "migration/sunset-window",
                        route.Id,
                        "warning",
                        $"sunset window open on route '{route.Id}' — old-implementation traffic stayed at or under " +
                        $"the retirement threshold {threshold} for {periods} periods " +
                        $"({string.Join(", ", window)}); delete the old handler and remove the route entry",
                        $"{tableFile}:{route.Line}"));
                }
                else if (window.Any(t => t > threshold))
                {
                    findings.Add(new CockpitFinding(
                        "migration/strangle-unfinished",
                        route.Id,
                        "error",
                        $"route '{route.Id}' is declared strangled but the fig has not strangled this route — " +
                        $"old-implementation traffic in the last {periods} periods exceeded the retirement " +
                        $"threshold {threshold} ({string.Join(", ", window)}); either route the remaining " +
                        "traffic through the new implementation or move the route back to strangling",
                        $"{tableFile}:{route.Line}"));
                }
            }

            routes.Add(new CockpitRouteView(
                route.Id,
                route.State,
                wiredSite is not null,
                route.Shadow,
                route.Traffic,
                threshold,
                periods,
                sunsetWindowOpen,
                wiredSite is null ? $"{tableFile}:{route.Line}" : $"{wiredSite.File}:{wiredSite.Line}"));
        }

        var declaredIds = config.Routes
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var site in wired.Where(w => !declaredIds.Contains(w.Id)))
        {
            findings.Add(new CockpitFinding(
                "migration/undeclared-route",
                site.Id,
                "warning",
                $"route '{site.Id}' is wired at the routing seam but not declared in the route table — " +
                "an undeclared route strangles nothing; declare it (state todo) or unwire it",
                $"{site.File}:{site.Line}"));
        }

        var ordered = findings
            .OrderBy(f => f.Severity == "error" ? 0 : 1)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.RouteId, StringComparer.Ordinal)
            .ToArray();

        return new CockpitReport(routes.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), ordered);
    }
}
