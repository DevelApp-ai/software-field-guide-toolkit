using System.Text;
using Sfg.Toolkit.ContextMap;
using Sfg.Toolkit.Core.Analysis;
using Sfg.Toolkit.Core.CodeTruth;
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
        case "--version":
            Console.WriteLine("sfg 0.2.0");
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

static string RequiredRoot(string[] args)
{
    string? root = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--config" or "--out" or "--evidence")
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

          sfg --version

        The scanner parses source only — no build, no restore. All output is
        deterministic for a given tree: no timestamps, sorted everywhere.
        """);
}
