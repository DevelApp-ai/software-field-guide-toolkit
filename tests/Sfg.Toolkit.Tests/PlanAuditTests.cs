using System.Text.Json;
using Sfg.Toolkit.PlanAudit;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// Plan-conformance auditor (#64): the diff parser anchors real line numbers,
/// the config is loud about ambiguous maps, the analyzer reconciles the two
/// diffs through the coverage map, the two discrepancy classes bite with
/// line-anchored citations, semantic matches resolve only with valid anchors
/// and are labeled, ranking is errors-first and deterministic, and the board
/// renders machine-checkable output.
/// </summary>
public sealed class PlanAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sfg-planaudit-tests", Guid.NewGuid().ToString("N"));

    public PlanAuditTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    // --- helpers ----------------------------------------------------------------

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static readonly string MapJson = """
        {
          "project": "src/QuoteTree/QuoteTree.csproj",
          "map": [
            { "spec": "specs/quote/", "code": "src/QuoteTree/Quote/" },
            { "spec": "specs/billing/", "code": "src/QuoteTree/Billing/" }
          ],
          "allowedUnmappedCode": [ "src/QuoteTree/QuoteTree.csproj" ]
        }
        """;

    private PlanAuditConfig Config(int? maxFindings = null)
    {
        var json = maxFindings is null ? MapJson : MapJson.Replace(
            "\"allowedUnmappedCode\"", $"\"maxFindings\": {maxFindings}, \"allowedUnmappedCode\"");
        return PlanAuditConfig.Load(Write("plan-audit.json", json));
    }

    /// <summary>An intent diff: the quote spec gains R4, the quote tests gain a criterion.</summary>
    private static readonly string ControlSpecDiff = """
        diff --git a/specs/quote/quote-order.ai b/specs/quote/quote-order.ai
        --- a/specs/quote/quote-order.ai
        +++ b/specs/quote/quote-order.ai
        @@ -8,3 +8,4 @@
         - R1: The total is the sum of all line items, two decimal places.
         - R2: A quote is valid for 30 days from the moment it is issued.
         - R3: A quote identifies the customer it was issued to.
        +- R4: Prices are quoted including VAT.
        diff --git a/specs/quote/quote-order.test b/specs/quote/quote-order.test
        --- a/specs/quote/quote-order.test
        +++ b/specs/quote/quote-order.test
        @@ -6,3 +6,5 @@
           then the quote is no longer valid.
         - Given a quote for customer "nordisk-mobler",
           then the quote carries the customer id.
        +- Given a line item priced 100.00 with 25% VAT,
        +  then the quoted price is 125.00.
        """ + "\n";

    /// <summary>An implementation diff: the quote service implements R4.</summary>
    private static readonly string ControlCodeDiff = """
        diff --git a/src/QuoteTree/Quote/QuoteService.cs b/src/QuoteTree/Quote/QuoteService.cs
        --- a/src/QuoteTree/Quote/QuoteService.cs
        +++ b/src/QuoteTree/Quote/QuoteService.cs
        @@ -6,9 +6,10 @@
         {
             public QuoteIssue Quote(IReadOnlyList<LineItem> items, string customerId)
             {
        -        var total = items.Sum(i => i.Price * i.Quantity);
        -        return new QuoteIssue(total, customerId, DateTime.UtcNow.AddDays(30));
        +        var vatRate = 0.25m;
        +        var total = items.Sum(i => i.Price * i.Quantity) * (1 + vatRate);
        +        return new QuoteIssue(total, customerId, DateTime.UtcNow.AddDays(30), vatRate);
             }
         }

        -public sealed record QuoteIssue(decimal Total, string CustomerId, DateTime ValidUntil);
        +public sealed record QuoteIssue(decimal Total, string CustomerId, DateTime ValidUntil, decimal VatRate);
        """ + "\n";

    /// <summary>An intent diff that also adds a billing requirement R4 — never implemented below.</summary>
    private static readonly string OmissionSpecDiff = """
        diff --git a/specs/billing/invoice.ai b/specs/billing/invoice.ai
        --- a/specs/billing/invoice.ai
        +++ b/specs/billing/invoice.ai
        @@ -7,3 +7,4 @@
         - R1: An invoice references exactly one accepted quote.
         - R2: An invoice states the total due and the payment deadline.
         - R3: A paid invoice is marked paid and never re-issued.
        +- R4: Invoices are exportable as PDF.
        """ + "\n";

    /// <summary>An implementation diff with a change the spec never approved.</summary>
    private static readonly string AdditionCodeDiff = """
        diff --git a/src/QuoteTree/Billing/InvoiceService.cs b/src/QuoteTree/Billing/InvoiceService.cs
        --- a/src/QuoteTree/Billing/InvoiceService.cs
        +++ b/src/QuoteTree/Billing/InvoiceService.cs
        @@ -4,7 +4,8 @@
         {
             public Invoice Issue(decimal totalDue, string quoteId, DateTime paymentDeadline)
             {
        -        return new Invoice(quoteId, totalDue, paymentDeadline, isPaid: false);
        +        var surpriseFee = totalDue * 0.01m;
        +        return new Invoice(quoteId, totalDue + surpriseFee, paymentDeadline, isPaid: false);
             }
         }
         
        """ + "\n";

    /// <summary>An implementation diff adding a whole new file.</summary>
    private static readonly string NewFileCodeDiff = """
        diff --git a/src/QuoteTree/Billing/AuditTrail.cs b/src/QuoteTree/Billing/AuditTrail.cs
        new file mode 100644
        --- /dev/null
        +++ b/src/QuoteTree/Billing/AuditTrail.cs
        @@ -0,0 +1,3 @@
        +namespace QuoteTree.Billing;
        +
        +public sealed class AuditTrail { }
        """ + "\n";

    /// <summary>An intent diff that changes only the quote spec (no test file).</summary>
    private static readonly string QuoteSpecDiff = """
        diff --git a/specs/quote/quote-order.ai b/specs/quote/quote-order.ai
        --- a/specs/quote/quote-order.ai
        +++ b/specs/quote/quote-order.ai
        @@ -8,3 +8,4 @@
         - R1: The total is the sum of all line items, two decimal places.
         - R2: A quote is valid for 30 days from the moment it is issued.
         - R3: A quote identifies the customer it was issued to.
        +- R4: Prices are quoted including VAT.
        """ + "\n";

    /// <summary>An implementation diff outside every mapped area (docs, not allowed).</summary>
    private static readonly string UnmappedCodeDiff = """
        diff --git a/docs/CHANGELOG.md b/docs/CHANGELOG.md
        --- a/docs/CHANGELOG.md
        +++ b/docs/CHANGELOG.md
        @@ -1,2 +1,3 @@
         # Changelog
         Some unreleased changes.
        +- one unreleased change
        """ + "\n";

    private PlanAuditReport Audit(
        PlanAuditConfig config,
        string specDiff,
        string codeDiff,
        IReadOnlyList<PlanAuditAnalyzer.SemanticMatch>? semantic = null)
    {
        var specFiles = UnifiedDiff.ParseText(specDiff, "spec.diff");
        var codeFiles = UnifiedDiff.ParseText(codeDiff, "code.diff");
        return PlanAuditAnalyzer.Analyze(config, specFiles, codeFiles, semantic);
    }

    // --- config: loud, never a guess ---------------------------------------------

    [Fact]
    public void Config_rejects_a_missing_file()
    {
        var error = Assert.Throws<FileNotFoundException>(
            () => PlanAuditConfig.Load(Path.Combine(_root, "absent.json")));
        Assert.Contains("plan-audit config not found", error.Message);
    }

    [Fact]
    public void Config_rejects_a_document_without_a_map()
    {
        var path = Write("bad.json", """{ "project": "src/App/App.csproj" }""");
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditConfig.Load(path));
        Assert.Contains("\"map\" array", error.Message);
    }

    [Fact]
    public void Config_rejects_an_empty_map()
    {
        var path = Write("bad.json", """{ "map": [] }""");
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditConfig.Load(path));
        Assert.Contains("declares no map entries", error.Message);
    }

    [Fact]
    public void Config_rejects_one_sided_map_entries()
    {
        var path = Write("bad.json", """{ "map": [ { "spec": "specs/quote/" } ] }""");
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditConfig.Load(path));
        Assert.Contains("\"code\"", error.Message);
    }

    [Fact]
    public void Config_rejects_overlapping_spec_prefixes()
    {
        var path = Write("bad.json", """
            { "map": [
              { "spec": "specs/", "code": "src/Quote/" },
              { "spec": "specs/billing/", "code": "src/Billing/" }
            ] }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditConfig.Load(path));
        Assert.Contains("spec prefixes overlap", error.Message);
    }

    [Fact]
    public void Config_rejects_overlapping_code_prefixes()
    {
        var path = Write("bad.json", """
            { "map": [
              { "spec": "specs/quote/", "code": "src/" },
              { "spec": "specs/billing/", "code": "src/QuoteTree/Billing/" }
            ] }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditConfig.Load(path));
        Assert.Contains("code prefixes overlap", error.Message);
    }

    [Fact]
    public void Config_rejects_an_impossible_max_findings()
    {
        var path = Write("bad.json", """{ "map": [ { "spec": "s/", "code": "c/" } ], "maxFindings": 0 }""");
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditConfig.Load(path));
        Assert.Contains("positive integer", error.Message);
    }

    [Fact]
    public void Config_normalizes_paths_and_matches_only_whole_prefixes()
    {
        var path = Write("good.json", """
            { "map": [ { "spec": "specs\\quote", "code": "src/QuoteTree/Quote/" } ] }
            """);
        var config = PlanAuditConfig.Load(path);
        Assert.Equal("specs/quote", config.Map[0].Spec);
        Assert.NotNull(config.SpecMapFor("specs/quote/quote-order.ai"));
        Assert.Null(config.SpecMapFor("specs/quote-extra/other.ai"));
    }

    // --- diff parsing: real line anchors, loud on garbage ------------------------

    [Fact]
    public void Diff_parses_a_two_file_diff_with_paths_and_anchors()
    {
        var files = UnifiedDiff.ParseText(ControlSpecDiff, "spec.diff");
        Assert.Equal(2, files.Count);
        Assert.Equal("specs/quote/quote-order.ai", files[0].Path);
        Assert.Equal(11, files[0].AddedLine);
        Assert.Equal("specs/quote/quote-order.test", files[1].Path);
        Assert.Equal(9, files[1].AddedLine);
    }

    [Fact]
    public void Diff_anchors_the_first_added_line_of_the_first_hunk()
    {
        var files = UnifiedDiff.ParseText(AdditionCodeDiff, "code.diff");
        Assert.Single(files);
        // the first '+' line of @@ -4,7 +4,8 @@ lands on new-file line 7
        Assert.Equal(7, files[0].AddedLine);
    }

    [Fact]
    public void Diff_marks_new_files_as_new_and_anchors_line_one()
    {
        var files = UnifiedDiff.ParseText(NewFileCodeDiff, "code.diff");
        Assert.Single(files);
        Assert.True(files[0].IsNew);
        Assert.Equal("src/QuoteTree/Billing/AuditTrail.cs", files[0].Path);
        Assert.Equal(1, files[0].AddedLine);
    }

    [Fact]
    public void Diff_marks_deleted_files_and_falls_back_to_the_hunk_start()
    {
        var deleted = """
            diff --git a/specs/quote/quote-order.test b/specs/quote/quote-order.test
            deleted file mode 100644
            --- a/specs/quote/quote-order.test
            +++ /dev/null
            @@ -1,8 +0,0 @@
            -# Quote orders - acceptance criteria
            -
            -- Given a cart with two line items 100.00 and 49.50,
            -  then the quoted total is 149.50.
            -- Given a quote issued 30+1 days ago,
            -  then the quote is no longer valid.
            -- Given a quote for customer "nordisk-mobler",
            -  then the quote carries the customer id.
            """;
        var files = UnifiedDiff.ParseText(deleted, "deleted.diff");
        Assert.Single(files);
        Assert.True(files[0].IsDeleted);
        Assert.Equal("specs/quote/quote-order.test", files[0].Path);
        Assert.Equal(0, files[0].AddedLine);
    }

    [Fact]
    public void Diff_resolves_renames_to_the_b_side()
    {
        var renamed = """
            diff --git a/src/QuoteTree/Old.cs b/src/QuoteTree/New.cs
            similarity index 90%
            rename from src/QuoteTree/Old.cs
            rename to src/QuoteTree/New.cs
            --- a/src/QuoteTree/Old.cs
            +++ b/src/QuoteTree/New.cs
            @@ -1,2 +1,2 @@
             namespace QuoteTree;
            -public sealed class Old { }
            +public sealed class New { }
            """;
        var files = UnifiedDiff.ParseText(renamed, "rename.diff");
        Assert.Single(files);
        Assert.Equal("src/QuoteTree/New.cs", files[0].Path);
    }

    [Fact]
    public void Diff_rejects_a_plus_header_without_a_minus_header()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => UnifiedDiff.ParseText("+++ b/a.cs\n@@ -1 +1 @@\n+x\n", "bad.diff"));
        Assert.Contains("line 1", error.Message);
        Assert.Contains("'+++' without a preceding '---'", error.Message);
    }

    [Fact]
    public void Diff_rejects_a_hunk_body_line_that_is_not_diff_syntax()
    {
        var garbage = """
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            xnot a diff line
            """;
        var error = Assert.Throws<InvalidOperationException>(
            () => UnifiedDiff.ParseText(garbage, "bad.diff"));
        Assert.Contains("line 5", error.Message);
    }

    [Fact]
    public void Diff_rejects_a_truncated_hunk()
    {
        var truncated = """
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            @@ -1,2 +1,2 @@
            +one line only
            """;
        var error = Assert.Throws<InvalidOperationException>(
            () => UnifiedDiff.ParseText(truncated, "bad.diff"));
        Assert.Contains("truncated hunk", error.Message);
    }

    [Fact]
    public void Diff_rejects_a_header_without_hunks()
    {
        var headerOnly = """
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            """;
        var error = Assert.Throws<InvalidOperationException>(
            () => UnifiedDiff.ParseText(headerOnly, "bad.diff"));
        Assert.Contains("no hunks", error.Message);
    }

    [Fact]
    public void Diff_rejects_a_file_that_is_not_a_diff_at_all()
    {
        Assert.Throws<InvalidOperationException>(
            () => UnifiedDiff.ParseText("hello world\n", "bad.diff"));
    }

    // --- analyzer: the two discrepancy classes bite -------------------------------

    [Fact]
    public void Control_is_silent_when_the_spec_change_is_implemented()
    {
        var report = Audit(Config(), ControlSpecDiff, ControlCodeDiff);
        Assert.Empty(report.Findings);
        Assert.Equal(2, report.Matches.Count);
        Assert.All(report.Matches, m => Assert.Equal("machine", m.Provenance));
        Assert.Equal(2, report.SpecFiles.Count);
        Assert.Single(report.CodeFiles);
    }

    [Fact]
    public void Omission_fires_when_an_approved_spec_change_is_not_implemented()
    {
        var report = Audit(Config(), ControlSpecDiff + OmissionSpecDiff, ControlCodeDiff);
        var omission = Assert.Single(report.Findings);
        Assert.Equal("plan/omission", omission.Rule);
        Assert.Equal("error", omission.Severity);
        Assert.Equal("specs/billing/invoice.ai", omission.SpecFile);
        Assert.Equal(10, omission.SpecLine);
    }

    [Fact]
    public void Unapproved_addition_fires_when_generated_code_has_no_spec_basis()
    {
        var report = Audit(Config(), ControlSpecDiff, ControlCodeDiff + AdditionCodeDiff);
        var addition = Assert.Single(report.Findings);
        Assert.Equal("plan/unapproved-addition", addition.Rule);
        Assert.Equal("error", addition.Severity);
        Assert.Equal("src/QuoteTree/Billing/InvoiceService.cs", addition.CodeFile);
        Assert.Equal(7, addition.CodeLine);
    }

    [Fact]
    public void Omission_and_addition_rank_before_warnings_and_deterministically()
    {
        var report = Audit(Config(), ControlSpecDiff + OmissionSpecDiff, ControlCodeDiff + UnmappedCodeDiff);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(1, report.Errors);
        Assert.Equal(1, report.Warnings);
        Assert.Equal("plan/omission", report.Findings[0].Rule);
        Assert.Equal("plan/unmapped-code", report.Findings[1].Rule);

        var again = Audit(Config(), ControlSpecDiff + OmissionSpecDiff, ControlCodeDiff + UnmappedCodeDiff);
        Assert.Equal(
            string.Join("|", report.Findings.Select(f => $"{f.Rule}:{f.Severity}:{f.SpecLine}:{f.CodeLine}")),
            string.Join("|", again.Findings.Select(f => $"{f.Rule}:{f.Severity}:{f.SpecLine}:{f.CodeLine}")));
    }

    [Fact]
    public void Unmapped_spec_and_code_change_are_warnings_with_anchors()
    {
        var config = PlanAuditConfig.Load(Write(
            "narrow.json", """{ "map": [ { "spec": "specs/quote/", "code": "src/QuoteTree/Quote/" } ] }"""));
        var report = Audit(config, OmissionSpecDiff, NewFileCodeDiff);
        Assert.Equal(2, report.Warnings);
        Assert.Equal(0, report.Errors);
        Assert.Contains(report.Findings, f => f.Rule == "plan/unmapped-spec" && f.SpecFile == "specs/billing/invoice.ai");
        Assert.Contains(report.Findings, f => f.Rule == "plan/unmapped-code" && f.CodeFile == "src/QuoteTree/Billing/AuditTrail.cs");
    }

    [Fact]
    public void Allowed_unmapped_code_stays_silent()
    {
        var config = PlanAuditConfig.Load(Write(
            "allowed.json",
            """{ "map": [ { "spec": "specs/quote/", "code": "src/QuoteTree/Quote/" } ], "allowedUnmappedCode": [ "src/QuoteTree/QuoteTree.csproj" ] }"""));
        var csprojDiff = """
            diff --git a/src/QuoteTree/QuoteTree.csproj b/src/QuoteTree/QuoteTree.csproj
            --- a/src/QuoteTree/QuoteTree.csproj
            +++ b/src/QuoteTree/QuoteTree.csproj
            @@ -3,3 +3,4 @@
               <PropertyGroup>
                 <TargetFramework>net8.0</TargetFramework>
            +    <LangVersion>latest</LangVersion>
               </PropertyGroup>
            """;
        var report = Audit(config, ControlSpecDiff, ControlCodeDiff + csprojDiff);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Findings_are_capped_errors_first()
    {
        // three spec changes with no code under their areas: three omissions,
        // plus an unmapped docs change — cap 2 renders the errors first
        var config = Config(maxFindings: 2);
        var report = Audit(config, ControlSpecDiff + OmissionSpecDiff, UnmappedCodeDiff);
        Assert.Equal(4, report.Findings.Count);
        Assert.Equal(2, report.RenderedFindings.Count);
        Assert.Equal(2, config.MaxFindings);
        Assert.All(report.RenderedFindings, f => Assert.Equal("error", f.Severity));
    }

    // --- semantic matches: labeled, anchor-validated, never a silent guess ------

    [Fact]
    public void A_valid_semantic_match_resolves_what_the_map_cannot()
    {
        var semantic = new[]
        {
            new PlanAuditAnalyzer.SemanticMatch(
                "specs/quote/quote-order.ai", 11,
                "src/QuoteTree/Billing/AuditTrail.cs", 1,
                "R4's audit trail lives outside the quote area")
        };
        var report = Audit(Config(), QuoteSpecDiff, NewFileCodeDiff, semantic);
        Assert.Empty(report.Findings);
        var match = Assert.Single(report.Matches);
        Assert.Equal("semantic", match.Provenance);
        Assert.Equal("R4's audit trail lives outside the quote area", match.Note);
    }

    [Fact]
    public void A_semantic_match_without_valid_anchors_is_rejected()
    {
        var unanchored = new[]
        {
            new PlanAuditAnalyzer.SemanticMatch("specs/quote/quote-order.ai", 4, "src/QuoteTree/Billing/AuditTrail.cs", 1, null)
        };
        var error = Assert.Throws<InvalidOperationException>(
            () => Audit(Config(), ControlSpecDiff, NewFileCodeDiff, unanchored));
        Assert.Contains("not a line the spec diff changes", error.Message);
    }

    [Fact]
    public void A_semantic_match_for_an_unchanged_file_is_rejected()
    {
        var invented = new[]
        {
            new PlanAuditAnalyzer.SemanticMatch("specs/quote/absent.ai", 1, "src/QuoteTree/Billing/AuditTrail.cs", 1, null)
        };
        var error = Assert.Throws<InvalidOperationException>(
            () => Audit(Config(), ControlSpecDiff, NewFileCodeDiff, invented));
        Assert.Contains("not a changed file in the approved spec diff", error.Message);
    }

    [Fact]
    public void Semantic_matches_load_from_the_optional_file()
    {
        var path = Write("semantic.json", """
            {
              "matches": [
                { "spec": "specs/quote/quote-order.ai:11", "code": "src/QuoteTree/Billing/AuditTrail.cs:1", "note": "ok" }
              ]
            }
            """);
        var matches = PlanAuditAnalyzer.LoadSemantic(path);
        var match = Assert.Single(matches);
        Assert.Equal("specs/quote/quote-order.ai", match.SpecFile);
        Assert.Equal(11, match.SpecLine);
        Assert.Equal(1, match.CodeLine);
        Assert.Equal("ok", match.Note);
    }

    [Fact]
    public void Semantic_file_must_declare_a_matches_array()
    {
        var path = Write("bad-semantic.json", """{ "pairs": [] }""");
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditAnalyzer.LoadSemantic(path));
        Assert.Contains("\"matches\" array", error.Message);
    }

    [Fact]
    public void Semantic_anchors_must_be_path_colon_line()
    {
        var path = Write("bad-semantic.json", """{ "matches": [ { "spec": "no-line-here", "code": "a.cs:1" } ] }""");
        var error = Assert.Throws<InvalidOperationException>(() => PlanAuditAnalyzer.LoadSemantic(path));
        Assert.Contains("'path:line'", error.Message);
    }

    // --- renderer: the board is cited, honest, and machine-checkable -------------

    [Fact]
    public void Board_carries_the_generator_provenance_and_citations()
    {
        var config = Config();
        var report = Audit(config, ControlSpecDiff + OmissionSpecDiff, ControlCodeDiff);
        var markdown = PlanAuditRenderer.Markdown(config, report);
        Assert.Contains(PlanAuditRenderer.Generator, markdown);
        Assert.Contains("plan/omission", markdown);
        Assert.Contains("specs/billing/invoice.ai:10", markdown);
        Assert.Contains("Advisory", markdown);
    }

    [Fact]
    public void Board_marks_a_conformant_audit()
    {
        var config = Config();
        var report = Audit(config, ControlSpecDiff, ControlCodeDiff);
        var markdown = PlanAuditRenderer.Markdown(config, report);
        Assert.Contains("Conformant", markdown);
    }

    [Fact]
    public void Json_board_parses_and_counts()
    {
        var config = Config();
        var report = Audit(config, ControlSpecDiff + OmissionSpecDiff, ControlCodeDiff);
        var json = PlanAuditRenderer.Json(config, report);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(PlanAuditRenderer.Generator, root.GetProperty("generator").GetString());
        Assert.Equal(3, root.GetProperty("specFiles").GetArrayLength());
        Assert.Equal(1, root.GetProperty("codeFiles").GetArrayLength());
        Assert.Equal(1, root.GetProperty("findings").GetArrayLength());
        Assert.Equal(1, root.GetProperty("errors").GetInt32());
        Assert.Equal(0, root.GetProperty("warnings").GetInt32());
        Assert.Equal("plan/omission", root.GetProperty("findings")[0].GetProperty("rule").GetString());
    }

    [Fact]
    public void The_cap_line_names_the_hidden_count()
    {
        var config = Config(maxFindings: 1);
        var report = Audit(config, ControlSpecDiff + OmissionSpecDiff, UnmappedCodeDiff);
        var markdown = PlanAuditRenderer.Markdown(config, report);
        Assert.Contains("…and 3 more", markdown);
    }
}
