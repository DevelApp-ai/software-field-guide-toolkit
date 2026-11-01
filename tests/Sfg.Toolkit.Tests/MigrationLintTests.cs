using Sfg.Toolkit.MigrationLint;
using System.Text.RegularExpressions;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// Migration linter (#67): classification is total and loud, the contract
/// steps are checked against the committed gates ledger (the gate fails
/// builds, not reviews), a removal needs a measured window and happens
/// deliberately late, naive backfills and destructive ordering bite, and all
/// rendering is deterministic with file:line evidence.
/// </summary>
public sealed class MigrationLintTests : IDisposable
{
    private static readonly Regex FileLine = new(@":[0-9]+$", RegexOptions.Compiled);

    private readonly MigrationAppFixture _app = new();

    public void Dispose() => _app.Dispose();

    // --- classification is total ------------------------------------------------

    [Fact]
    public void Parser_classifies_expand_migrate_and_contract()
    {
        var report = _app.Lint();
        var initial = Assert.Single(report.Migrations, m => m.FileName == "20260101100000_InitialCreate.cs");
        Assert.Equal(2, initial.Count(MigrationKind.Expand));
        var backfill = Assert.Single(report.Migrations, m => m.FileName == "20260310100000_BackfillDeliveryAddress.cs");
        Assert.Equal(1, backfill.Count(MigrationKind.Migrate));
        var remove = Assert.Single(report.Migrations, m => m.FileName == "20260601100000_RemoveShippingAddress.cs");
        Assert.Equal(1, remove.Count(MigrationKind.Contract));
        Assert.Equal(5, report.StatementCount);
    }

    [Fact]
    public void Parser_rejects_unparseable_file_names()
    {
        _app.WriteMigration("RemoveShippingAddress.cs", """
            public partial class RemoveShippingAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(name: "ShippingAddress", table: "Orders");
                }
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Lint());
        Assert.Contains("<14-digit-timestamp>_<Name>.cs", error.Message);
    }

    [Fact]
    public void Parser_rejects_duplicate_timestamps()
    {
        _app.WriteMigration("20260601100000_Duplicate.cs", """
            public partial class Duplicate
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.AddColumn<string>(name: "Note", table: "Orders", nullable: true);
                }
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Lint());
        Assert.Contains("duplicate migration timestamp 20260601100000", error.Message);
    }

    [Fact]
    public void Parser_rejects_migration_without_Up()
    {
        _app.WriteMigration("20260602100000_NoUp.cs", """
            public partial class NoUp
            {
                protected override void Down(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(name: "Note", table: "Orders");
                }
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Lint());
        Assert.Contains("no Up method", error.Message);
    }

    [Fact]
    public void Parser_rejects_unknown_migrationBuilder_calls()
    {
        _app.WriteMigration("20260602100000_UnknownCall.cs", """
            public partial class UnknownCall
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.EnsureSchema(name: "audit");
                }
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Lint());
        Assert.Contains("unrecognized migrationBuilder call 'EnsureSchema'", error.Message);
        Assert.Contains("20260602100000_UnknownCall.cs", error.Message);
    }

    [Fact]
    public void Parser_rejects_missing_migrations_directory()
    {
        Directory.Delete(Path.Combine(_app.Root, "Migrations"), recursive: true);
        var error = Assert.Throws<DirectoryNotFoundException>(() => _app.Lint());
        Assert.Contains("migrations directory not found", error.Message);
    }

    [Fact]
    public void Gates_loader_is_loud_on_malformed_passes()
    {
        _app.Write("migration-gates.json", """
            { "passes": [ { "object": "Orders", "gate": "forward-compat" } ] }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Lint());
        Assert.Contains("\"Table.Column\"", error.Message);
    }

    // --- the control stays silent -------------------------------------------------

    [Fact]
    public void Clean_history_is_silent()
    {
        var report = _app.Lint();
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Errors);
        Assert.NotNull(report.Gates);
        var pass = Assert.Single(report.Gates.Passes);
        Assert.Equal("Orders.ShippingAddress", pass.Object);
    }

    // --- naive backfill ------------------------------------------------------------

    [Fact]
    public void Naive_backfill_bites()
    {
        _app.WriteMigration("20260310100000_BackfillDeliveryAddress.cs", """
            public partial class BackfillDeliveryAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql("UPDATE Orders SET DeliveryAddress = ShippingAddress");
                }
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/naive-backfill");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("lock escalation", finding.Message);
        Assert.Contains("WAL flooding", finding.Message);
        Assert.Contains("replication lag", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    [Fact]
    public void Batched_backfill_is_clean()
    {
        // the control fixture backfill carries WHERE Id BETWEEN — no finding
        var report = _app.Lint();
        Assert.DoesNotContain(report.Findings, f => f.Rule == "migration/naive-backfill");
    }

    // --- destructive-first ordering --------------------------------------------------

    [Fact]
    public void Destructive_first_bites_intra_file()
    {
        _app.WriteMigration("20260601100000_RemoveShippingAddress.cs", """
            public partial class RemoveShippingAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(name: "ShippingAddress", table: "Orders");
                    migrationBuilder.Sql("UPDATE Orders SET Note = ShippingAddress");
                }
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/destructive-first");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("Orders.ShippingAddress", finding.Message);
        Assert.Equal(2, finding.Evidence.Count);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    [Fact]
    public void Destructive_first_bites_inter_file()
    {
        _app.WriteMigration("20260701100000_ReAddShippingAddress.cs", """
            public partial class ReAddShippingAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.AddColumn<string>(name: "ShippingAddress", table: "Orders", nullable: true);
                }
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/destructive-first");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("added back", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    // --- gates and windows -----------------------------------------------------------

    [Fact]
    public void Contract_without_gate_bites_when_the_ledger_is_absent()
    {
        File.Delete(_app.GatesPath);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/contract-without-gate");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("the gate fails builds, not reviews", finding.Message);
        Assert.Contains("absent", finding.Message);
    }

    [Fact]
    public void Contract_without_gate_bites_without_a_pass()
    {
        _app.WriteMigration("20260602100000_RemoveLegacyTotal.cs", """
            public partial class RemoveLegacyTotal
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(name: "LegacyTotal", table: "Orders");
                }
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/contract-without-gate");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("Orders.LegacyTotal", finding.Message);
        Assert.Contains("no gate pass", finding.Message);
        Assert.Equal(2, finding.Evidence.Count);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    [Fact]
    public void Removal_without_window_bites_on_unmeasured_passes()
    {
        _app.Write("migration-gates.json", """
            {
              "passes": [
                {
                  "object": "Orders.ShippingAddress",
                  "gate": "forward-compat",
                  "expandedAt": "2026-03-01",
                  "consumers": [ "ShippingService" ]
                }
              ]
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/removal-without-window");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("no measured window", finding.Message);
        Assert.Contains("seven consecutive days", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    [Fact]
    public void Removal_without_window_bites_on_inverted_windows()
    {
        _app.Write("migration-gates.json", """
            {
              "passes": [
                {
                  "object": "Orders.ShippingAddress",
                  "gate": "forward-compat",
                  "expandedAt": "2026-03-01",
                  "coldAt": "2026-01-01",
                  "consumers": [ "ShippingService" ]
                }
              ]
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/removal-without-window");
        Assert.Contains("inverted window", finding.Message);
    }

    [Fact]
    public void Removal_before_cold_bites()
    {
        _app.Write("migration-gates.json", """
            {
              "passes": [
                {
                  "object": "Orders.ShippingAddress",
                  "gate": "forward-compat",
                  "expandedAt": "2026-03-01",
                  "coldAt": "2026-07-01",
                  "consumers": [ "ShippingService" ]
                }
              ]
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/removal-before-cold");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("2026-06-01", finding.Message);
        Assert.Contains("2026-07-01", finding.Message);
        Assert.Contains("deliberately late", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    [Fact]
    public void A_mid_window_pass_without_a_removal_is_legal()
    {
        // expandedAt recorded, no coldAt yet, and nothing was removed: mid-window
        _app.WriteMigration("20260301100000_AddDeliveryAddress.cs", """
            public partial class AddDeliveryAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.AddColumn<string>(name: "DeliveryAddress", table: "Orders", nullable: true);
                }
            }
            """);
        _app.Write("migration-gates.json", """
            {
              "passes": [
                {
                  "object": "Orders.ShippingAddress",
                  "gate": "forward-compat",
                  "expandedAt": "2026-03-01",
                  "consumers": [ "ShippingService" ]
                }
              ]
            }
            """);
        // remove the removal: the window is open, nothing to check
        File.Delete(Path.Combine(_app.Root, "Migrations", "20260601100000_RemoveShippingAddress.cs"));
        var report = _app.Lint();
        Assert.Empty(report.Findings);
    }

    // --- one migration, one revert ----------------------------------------------------

    [Fact]
    public void Drop_not_isolated_warns()
    {
        _app.WriteMigration("20260601100000_RemoveShippingAddress.cs", """
            public partial class RemoveShippingAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(name: "ShippingAddress", table: "Orders");
                    migrationBuilder.AddColumn<string>(name: "Note", table: "Orders", nullable: true);
                }
            }
            """);
        var report = _app.Lint();
        var finding = Assert.Single(report.Findings, f => f.Rule == "migration/drop-not-isolated");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("one migration, one revert", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    // --- rendering ----------------------------------------------------------------------

    [Fact]
    public void Markdown_renders_the_timeline_windows_and_citations()
    {
        var markdown = MigrationLintRenderer.Markdown(_app.Config(), _app.Lint());
        Assert.Contains("sfg migration-lint 0.12.0", markdown);
        Assert.Contains("`20260601100000_RemoveShippingAddress.cs`", markdown);
        Assert.Contains("`Orders.ShippingAddress`", markdown);
        Assert.Contains("2026-05-25", markdown);
        Assert.Contains("Clean history", markdown);

        // with a finding, the citation format is the CI grep: `:[0-9]+`
        _app.WriteMigration("20260310100000_BackfillDeliveryAddress.cs", """
            public partial class BackfillDeliveryAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql("UPDATE Orders SET DeliveryAddress = ShippingAddress");
                }
            }
            """);
        var cited = MigrationLintRenderer.Markdown(_app.Config(), _app.Lint());
        Assert.Matches(new Regex(@":[0-9]+`", RegexOptions.None), cited);
    }

    [Fact]
    public void Json_renders_the_machine_summary()
    {
        var json = MigrationLintRenderer.Json(_app.Config(), _app.Lint());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("sfg migration-lint 0.12.0", root.GetProperty("generator").GetString());
        Assert.Equal(4, root.GetProperty("migrations").GetArrayLength());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
        Assert.Equal("2026-05-25", root.GetProperty("gates")[0].GetProperty("coldAt").GetString());
    }

    [Fact]
    public void Renderer_is_deterministic()
    {
        _app.WriteMigration("20260310100000_BackfillDeliveryAddress.cs", """
            public partial class BackfillDeliveryAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql("UPDATE Orders SET DeliveryAddress = ShippingAddress");
                }
            }
            """);
        var first = MigrationLintRenderer.Markdown(_app.Config(), _app.Lint());
        var second = MigrationLintRenderer.Markdown(_app.Config(), _app.Lint());
        Assert.Equal(first, second);
        Assert.Equal(MigrationLintRenderer.Json(_app.Config(), _app.Lint()),
            MigrationLintRenderer.Json(_app.Config(), _app.Lint()));
    }

    [Fact]
    public void Findings_rank_errors_first()
    {
        _app.WriteMigration("20260601100000_RemoveShippingAddress.cs", """
            public partial class RemoveShippingAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(name: "ShippingAddress", table: "Orders");
                    migrationBuilder.Sql("INSERT INTO OrderArchive (OrderId, ShippingLine) SELECT Id, ShippingAddress FROM Orders");
                }
            }
            """);
        _app.WriteMigration("20260310100000_BackfillDeliveryAddress.cs", """
            public partial class BackfillDeliveryAddress
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql("UPDATE Orders SET DeliveryAddress = ShippingAddress");
                }
            }
            """);
        var report = _app.Lint();
        Assert.Equal(2, report.Errors);
        Assert.Equal(1, report.Warnings);
        Assert.Equal("error", report.Findings[0].Severity);
        Assert.Equal("error", report.Findings[1].Severity);
        Assert.Equal("warning", report.Findings[2].Severity);
    }
}
