using System.Text;
using Sfg.Toolkit.Cockpit;
using Sfg.Toolkit.ContextMap;
using Sfg.Toolkit.EventHarness;
using Sfg.Toolkit.Core.Analysis;
using Sfg.Toolkit.Core.CodeTruth;
using Sfg.Toolkit.PlanAudit;
using Sfg.Toolkit.EvalHarness;
using Sfg.Toolkit.BiasProbe;
using Sfg.Toolkit.MigrationLint;
using Sfg.Toolkit.DoraMetrics;
using Sfg.Toolkit.ReviewCalib;
using Sfg.Toolkit.SeamLedger;
using Sfg.Toolkit.TenantProbes;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    switch (args[0])
    {
        case "context-map":
            return RunContextMap(args[1..]);
        case "seam-ledger":
            return RunSeamLedger(args[1..]);
        case "tenant-probes":
            return RunTenantProbes(args[1..]);
        case "cockpit":
            return RunCockpit(args[1..]);
        case "events":
            return RunEvents(args[1..]);
        case "plan-audit":
            return RunPlanAudit(args[1..]);
        case "eval":
            return RunEval(args[1..]);
        case "migration-lint":
            return RunMigrationLint(args[1..]);
        case "dora":
            return RunDora(args[1..]);
        case "review-calib":
            return RunReviewCalib(args[1..]);
        case "bias-probe":
            return RunBiasProbe(args[1..]);
        case "--version":
            Console.WriteLine("sfg 0.10.0");
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            PrintHelp();
            return 2;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static int RunMigrationLint(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config");
    var gatesOverride = Option(args, "--gates");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "migration-lint");
    var strict = HasFlag(args, "--strict");

    var config = MigrationLintConfig.Load(configPath, root, gatesOverride);
    var migrations = MigrationParser.Parse(config.MigrationsFullPath);
    var report = MigrationLintAnalyzer.Analyze(config, migrations);

    WriteFile(outDir, "migration-lint.md", MigrationLintRenderer.Markdown(config, report));
    WriteFile(outDir, "migration-lint.json", MigrationLintRenderer.Json(config, report));

    Console.WriteLine($"migration-lint: {report.Migrations.Count} migration(s) in {config.MigrationsDir}/, " +
                      $"{report.StatementCount} statement(s) — {report.Count(MigrationKind.Expand)} expand, " +
                      $"{report.Count(MigrationKind.Migrate)} migrate, {report.Count(MigrationKind.Contract)} contract, " +
                      $"{(report.Gates is null ? "no gates ledger" : $"{report.Gates.Passes.Count} gate pass(es)")}, " +
                      $"{report.Findings.Count} finding(s) ({report.Errors} errors, {report.Warnings} warnings) — advisory: exit 0");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "migration-lint.md")}, {Path.Combine(outDir, "migration-lint.json")}");
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunEval(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config") ?? Path.Combine(root, "eval.json");
    var resultsPath = Option(args, "--results");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "eval");
    var strict = HasFlag(args, "--strict");
    var updateBaseline = HasFlag(args, "--update-baseline");

    var config = EvalHarnessConfig.Load(configPath);
    var report = EvalAnalyzer.Analyze(config, resultsPath);

    WriteFile(outDir, "eval.md", EvalRenderer.Markdown(config, report));
    WriteFile(outDir, "eval.json", EvalRenderer.Json(config, report));

    if (updateBaseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.BaselinePath)) ?? ".");
        File.WriteAllText(config.BaselinePath, EvalRenderer.BaselineJson(config, report), new UTF8Encoding(false));
    }

    Console.WriteLine($"eval: suite {config.Name}, run {report.RunName}, {report.Cases.Count} case(s), " +
                      $"{report.Findings.Count} finding(s) ({report.Errors} errors, {report.Warnings} warnings), " +
                      $"judge agreement {report.Agreement:0.####} on {report.LabeledSampleSize} labeled case(s)");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "eval.md")}, {Path.Combine(outDir, "eval.json")}" +
                      (updateBaseline ? $", baseline → {config.BaselinePath}" : string.Empty));
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunPlanAudit(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config") ?? Path.Combine(root, "plan-audit.json");
    var specDiffPath = Option(args, "--spec-diff");
    var codeDiffPath = Option(args, "--code-diff");
    var semanticPath = Option(args, "--semantic");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "plan-audit");
    var strict = HasFlag(args, "--strict");

    if (specDiffPath is null || codeDiffPath is null)
    {
        throw new ArgumentException(
            "plan-audit needs both diffs: --spec-diff <intent-PR.diff> --code-diff <implementation-PR.diff>");
    }

    var config = PlanAuditConfig.Load(configPath);
    var specFiles = UnifiedDiff.Parse(specDiffPath);
    var codeFiles = UnifiedDiff.Parse(codeDiffPath);
    var semantic = PlanAuditAnalyzer.LoadSemantic(semanticPath);
    var report = PlanAuditAnalyzer.Analyze(config, specFiles, codeFiles, semantic);

    WriteFile(outDir, "plan-audit.md", PlanAuditRenderer.Markdown(config, report));
    WriteFile(outDir, "plan-audit.json", PlanAuditRenderer.Json(config, report));

    Console.WriteLine($"plan-audit: {report.SpecFiles.Count} spec change(s), {report.CodeFiles.Count} code change(s), " +
                      $"{report.Matches.Count} match(es), {report.Findings.Count} finding(s) " +
                      $"({report.Errors} errors, {report.Warnings} warnings) — advisory: exit 0");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "plan-audit.md")}, {Path.Combine(outDir, "plan-audit.json")}");
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunContextMap(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "context-map");
    var strict = HasFlag(args, "--strict");

    var config = SfgConfig.Load(configPath);
    var scan = CodeTruthScanner.Scan(root, config);
    var report = new BoundaryAnalyzer().Analyze(scan, config);

    WriteFile(outDir, "context-map.drawio", ContextMapRenderer.Drawio(report));
    WriteFile(outDir, "context-map.json", ContextMapRenderer.Json(report));
    WriteFile(outDir, "findings.md", ContextMapRenderer.FindingsMarkdown(report));

    Console.WriteLine($"context-map: {report.Contexts.Count} contexts, {report.Edges.Count} edges, " +
                      $"{report.Findings.Count} findings ({report.Errors} errors, {report.Warnings} warnings)");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "context-map.drawio")}, {Path.Combine(outDir, "context-map.json")}, {Path.Combine(outDir, "findings.md")}");
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunSeamLedger(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config");
    var evidencePath = Option(args, "--evidence");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "seam-ledger");

    var config = SfgConfig.Load(configPath);
    var evidence = EvidenceStore.Load(evidencePath);
    var scan = CodeTruthScanner.Scan(root, config);
    var report = new SeamLedgerAnalyzer().Analyze(scan, config, evidence);

    WriteFile(outDir, "seam-ledger.json", SeamLedgerRenderer.LedgerJson(report));
    WriteFile(outDir, "collapse-review.md", SeamLedgerRenderer.CollapseReviewMarkdown(report));

    Console.WriteLine($"seam-ledger: {report.Seams.Count} seams, {report.DueForCollapse} due for collapse, " +
                      $"{report.EvidenceMissing} missing evidence");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "seam-ledger.json")}, {Path.Combine(outDir, "collapse-review.md")}");
    return 0;
}

static int RunTenantProbes(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "tenant-probes");

    var config = TenantProbeConfig.Load(configPath);
    var model = EfModelScanner.Scan(root, config);
    var suite = TenantProbeGenerator.Generate(root, outDir, model, config);

    foreach (var file in suite.Files)
    {
        WriteFile(outDir, file.Path, file.Content);
    }

    Console.WriteLine($"tenant-probes: DbContext {model.Context.Name} ({model.Context.File}:{model.Context.Line}), " +
                      $"{suite.Entities.Count} probed entities, {suite.Files.Count} generated files");
    Console.WriteLine($"outputs: {outDir}/ (run with: dotnet test {Path.Combine(outDir, "TenantProbes.csproj")})");
    return 0;
}

static int RunCockpit(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config") ?? Path.Combine(root, "migration.json");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "cockpit");
    var strict = HasFlag(args, "--strict");

    var config = MigrationConfig.Load(configPath);
    var wired = PivotScanner.Scan(root, config.RouteMethod);
    var report = new CockpitAnalyzer().Analyze(config, wired, Path.GetFileName(configPath));

    WriteFile(outDir, "cockpit.json", CockpitRenderer.Json(report, config));
    WriteFile(outDir, "cockpit.md", CockpitRenderer.Markdown(report, config));
    WriteFile(outDir, "cockpit.drawio", CockpitRenderer.Drawio(report));

    var shadowCount = 0;
    if (config.Routes.Any(r => r.Shadow))
    {
        var handlers = HandlerScanner.Scan(root);
        var suite = ShadowDiffGenerator.Generate(root, outDir, config, handlers);
        foreach (var file in suite.Files)
        {
            WriteFile(outDir, file.Path, file.Content);
        }

        shadowCount = suite.Files.Count;
    }

    Console.WriteLine($"cockpit: {report.Routes.Count} routes ({report.Routes.Count(r => r.State == "todo")} todo, " +
                      $"{report.Routes.Count(r => r.State == "strangling")} strangling, {report.Routes.Count(r => r.State == "strangled")} strangled), " +
                      $"{report.Findings.Count} findings ({report.Errors} errors, {report.Warnings} warnings), " +
                      $"{shadowCount} shadow-suite files");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "cockpit.md")}, {Path.Combine(outDir, "cockpit.json")}, {Path.Combine(outDir, "cockpit.drawio")}" +
                      (shadowCount > 0 ? $", {outDir}/ (shadow suite: dotnet test {Path.Combine(outDir, "ShadowDiff.csproj")})" : string.Empty));
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunEvents(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config") ?? Path.Combine(root, "events.json");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "events");

    var config = EventHarnessConfig.Load(configPath);
    var scan = ConventionScanner.Scan(root, config);
    var suite = EventHarnessGenerator.Generate(root, outDir, config, scan);

    foreach (var file in suite.Files)
    {
        WriteFile(outDir, file.Path, file.Content);
    }

    WriteFile(outDir, "events.md", EventHarnessRenderer.Markdown(config, scan, suite));
    WriteFile(outDir, "events.json", EventHarnessRenderer.Json(config, scan, suite));

    Console.WriteLine($"events: consumer {scan.Consumer.Name} ({scan.Consumer.File}:{scan.Consumer.Line}), " +
                      $"{config.Events.Count} events, {suite.Files.Count} suite files" +
                      (scan.Outbox is null ? string.Empty : $", outbox {scan.Outbox.Name} ({scan.Outbox.File}:{scan.Outbox.Line})"));
    Console.WriteLine($"outputs: {Path.Combine(outDir, "events.md")}, {Path.Combine(outDir, "events.json")}, " +
                      $"{outDir}/ (suite: dotnet test {Path.Combine(outDir, "EventConformance.csproj")})");
    return 0;
}

static int RunDora(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config");
    var eventsOverride = Option(args, "--events");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "dora");
    var strict = HasFlag(args, "--strict");

    var config = DoraConfig.Load(configPath, root, eventsOverride);
    var export = DoraAnalyzer.Load(config);
    var report = DoraAnalyzer.Analyze(config, export);

    WriteFile(outDir, "dora.md", DoraRenderer.Markdown(config, export, report));
    WriteFile(outDir, "dora.json", DoraRenderer.Json(config, export, report));
    WriteFile(outDir, "dora-definitions.json", DoraRenderer.DefinitionsJson());

    Console.WriteLine($"dora: {report.Deployments.Count} deployment(s) ({report.ExcludedDeployments.Count} re-run(s) excluded), " +
                      $"{report.Changes.Count} change(s), {report.Builds.Count} build(s), {report.Failures.Count} failure(s) — " +
                      $"{report.Joins.Count} join(s) verified, {report.Series.Count} period(s) trended, " +
                      $"{report.Findings.Count} finding(s) ({report.Errors} errors, {report.Warnings} warnings) — advisory: exit 0");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "dora.md")}, {Path.Combine(outDir, "dora.json")}, {Path.Combine(outDir, "dora-definitions.json")}");
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunReviewCalib(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config");
    var eventsOverride = Option(args, "--events");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "review-calib");
    var strict = HasFlag(args, "--strict");

    var config = ReviewCalibConfig.Load(configPath, root, eventsOverride);
    var export = ReviewCalibAnalyzer.Load(config);
    var report = ReviewCalibAnalyzer.Analyze(config, export);

    WriteFile(outDir, "review-calib.md", ReviewCalibRenderer.Markdown(config, export, report));
    WriteFile(outDir, "review-calib.json", ReviewCalibRenderer.Json(config, export, report));

    Console.WriteLine($"review-calib: {report.Reviews.Count} review(s) ({report.SeededCount} seeded, {report.CaughtCount} caught), " +
                      $"{report.Roles.Count} role(s), {report.Queues.Count} queue(s) — " +
                      (report.JudgeSample == 0
                          ? "no judged row"
                          : $"judge agreement {report.Agreement:0.####} on {report.JudgeSample} judged row(s)") +
                      $", {report.Findings.Count} finding(s) ({report.Errors} errors, {report.Warnings} warnings) — advisory: exit 0");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "review-calib.md")}, {Path.Combine(outDir, "review-calib.json")}");
    return strict && report.Errors > 0 ? 1 : 0;
}

static int RunBiasProbe(string[] args)
{
    var root = RequiredRoot(args);
    var configPath = Option(args, "--config") ?? Path.Combine(root, "bias-probe.json");
    var resultsPath = Option(args, "--results");
    var outDir = Option(args, "--out") ?? Path.Combine("sfg-out", "bias-probe");
    var strict = HasFlag(args, "--strict");
    var updateBaseline = HasFlag(args, "--update-baseline");

    var config = BiasProbeConfig.Load(configPath);
    var report = BiasProbeAnalyzer.Analyze(config, resultsPath);

    WriteFile(outDir, "bias-probe.md", BiasProbeRenderer.Markdown(config, report));
    WriteFile(outDir, "bias-probe.json", BiasProbeRenderer.Json(config, report));

    if (updateBaseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.BaselinePath)) ?? ".");
        File.WriteAllText(config.BaselinePath, BiasProbeRenderer.BaselineJson(config, report), new UTF8Encoding(false));
    }

    Console.WriteLine($"bias-probe: suite {config.Name}, run {report.RunName}, " +
                      $"{report.PairConsistency.Count} measured pair(s) (consistency {report.Consistency:0.####}), " +
                      $"{report.Cases.Count} case(s), {report.Findings.Count} finding(s) ({report.Errors} errors, {report.Warnings} warnings), " +
                      $"judge agreement {report.Agreement:0.####} on {report.LabeledSampleSize} labeled case(s)");
    Console.WriteLine($"outputs: {Path.Combine(outDir, "bias-probe.md")}, {Path.Combine(outDir, "bias-probe.json")}" +
                      (updateBaseline ? $", baseline → {config.BaselinePath}" : string.Empty));
    return strict && report.Errors > 0 ? 1 : 0;
}

static string RequiredRoot(string[] args)
{
    string? root = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--config" or "--out" or "--evidence" or "--spec-diff" or "--code-diff" or "--semantic" or "--results" or "--gates" or "--events")
        {
            i++;
            continue;
        }

        if (args[i].StartsWith("--"))
        {
            continue;
        }

        if (root is null)
        {
            root = args[i];
        }
    }

    if (root is null)
    {
        throw new ArgumentException("missing repository root path (try: sfg context-map <path>)");
    }

    return root;
}

static string? Option(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

static bool HasFlag(string[] args, string name) => args.Contains(name);

static void WriteFile(string directory, string fileName, string content)
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, fileName), content, new UTF8Encoding(false));
}

static void PrintHelp()
{
    Console.WriteLine("""
        sfg — Software Field Guide companion tools

        usage:
          sfg context-map <root> [--config <sfg.json>] [--out <dir>] [--strict]
              Extracts context boundaries from code truth and emits:
              context-map.drawio, context-map.json, findings.md.
              --strict exits non-zero on error-severity findings.

          sfg seam-ledger <root> [--config <sfg.json>] [--evidence <file>] [--out <dir>]
              Inventories seams (interfaces, plugin contracts, event hooks) and emits:
              seam-ledger.json, collapse-review.md.

          sfg tenant-probes <root> [--config <tenant-probes.json>] [--out <dir>]
              Generates a compilable xunit probe suite for tenant isolation (#58):
              cross-tenant reads, fail-closed saves, pool-checkout leakage,
              disabled-state fail-closed. Run it with: dotnet test.

          sfg cockpit <root> [--config <migration.json>] [--out <dir>] [--strict]
              Analyzes the strangler-fig migration from the route table (#56):
              routing-seam coverage, shadow-diff wiring, and the sunset
              tripwire. Emits cockpit.md, cockpit.json, cockpit.drawio, and a
              compilable shadow-diff xunit suite for shadow routes.
              --strict exits non-zero on error-severity findings.

          sfg events <root> [--config <events.json>] [--out <dir>]
              Generates a compilable xunit conformance suite for message
              contracts (#60): duplicate delivery, out-of-order arrival,
              poison payloads (dead letters, never a crash), old-schema
              producer replay, and outbox health on the producer side.
              Emits events.md, events.json, and the suite — run with:
              dotnet test.

          sfg plan-audit <root> --spec-diff <intent.diff> --code-diff <impl.diff>
                          [--config <plan-audit.json>] [--semantic <matches.json>]
                          [--out <dir>] [--strict]
              Audits the implementation diff against the approved spec diff
              (#64): reports omissions (spec changes with no generated
              change) and unapproved additions (generated changes with no
              spec basis), line-anchored on both sides. Advisory by default;
              --strict exits non-zero on error-severity findings. Emits
              plan-audit.md and plan-audit.json.

          sfg eval <root> [--config <eval.json>] [--results <results.json>]
                      [--out <dir>] [--strict] [--update-baseline]
              Runs the LLM evaluation harness (#66): matches a recorded run
              against the committed labeled set, aggregates per-stage
              metrics, publishes judge agreement against the human-labeled
              sample, and gates the run against the committed baseline — a
              regression cites the cases that moved. --update-baseline
              rewrites the baseline file from this run (commit it
              deliberately); --strict exits non-zero on error-severity
              findings. Emits eval.md and eval.json.

          sfg migration-lint <root> [--config <migration-lint.json>]
                              [--gates <migration-gates.json>] [--out <dir>]
                              [--strict]
              Lints the committed migration history (#67): classifies every
              statement expand (additive), migrate (rewiring), or contract
              (removing/tightening), and checks each contract step against the
              gates ledger — a removal needs a measured window (expanded, then
              cold) and happens deliberately late. Flags destructive-first
              ordering and naive unbatched backfills. Advisory by default;
              --strict exits non-zero on error-severity findings. Emits
              migration-lint.md and migration-lint.json.

          sfg dora <root> [--config <dora.json>] [--events <dora-events.json>]
                          [--out <dir>] [--strict]
              Extracts the DORA four keys (#68) from a recorded events export —
              never an API live: wiring a live system means producing an export
              in the documented format. Verifies every join the keys depend on
              (deployment → change → build, failure → restore evidence), flags
              re-runs mislabeled as new deployments, and trends deployment
              frequency, lead time, change failure rate, and restore time per
              day, ISO week, or month against the versioned definitions file —
              trends, not scores. Advisory by default; --strict exits non-zero
              on error-severity findings. Emits dora.md, dora.json, and
              dora-definitions.json.

          sfg review-calib <root> [--config <review-calib.json>]
                            [--events <review-events.json>] [--out <dir>] [--strict]
              Measures the human reviewers of the queues (#77): reads a
              recorded export of review decisions — never an API live — with
              seeded defects planted in it, so catch rates per role and
              queue approval telemetry replace vibes. Flags a seeded defect
              approved cleanly, a queue that rubber-stamps (99.8%+ approval
              at sub-second median latency — a broken loop), a role below the
              catch-rate floor, sub-second approvals, and stale calibration
              evidence (no seeded decision in the window). Exports the rung
              evidence the autonomy ladder is earned on, and human-vs-judge
              disagreement — a calibration instrument, never a leaderboard.
              Advisory by default; --strict exits non-zero on error-severity
              findings. Emits review-calib.md and review-calib.json.

          sfg bias-probe <root> [--config <bias-probe.json>]
                            [--results <results.json>] [--out <dir>]
                            [--strict] [--update-baseline]
              Runs the bias probe pack (#78) on a recorded eval run —
              never a model live: it judges recorded outcomes. Measures
              counterfactual consistency over the committed pair pack (the
              same input with only the marker swapped must score the same),
              cohort-slices the eval metrics so a cohort cannot hide inside
              the aggregate, checks refusal symmetry (should-succeed cases
              succeed for every cohort alike; must-refuse cases refuse
              regardless of cohort), measures citation balance against the
              declared corpus shares, and calibrates the judge against the
              human-labeled sample — abstentions never count.
              --update-baseline rewrites the baseline file from this run
              (commit it deliberately); --strict exits non-zero on
              error-severity findings. Emits bias-probe.md and
              bias-probe.json.

          sfg --version

        The scanner parses source only — no build, no restore. All output is
        deterministic for a given tree: no timestamps, sorted everywhere.
        """);
}

