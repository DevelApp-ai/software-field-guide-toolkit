using Sfg.Toolkit.MigrationLint;
namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic migration history (#67): a clean expand → backfill → contract
/// sequence with a measured, gated window — the control — plus helpers to seed
/// the failure modes (naive backfills, destructive ordering, ungated removals,
/// unmeasured windows, drops before cold, mixed migrations) so the tests can
/// prove the linter bites and never passes silently.
/// </summary>
public sealed class MigrationAppFixture : IDisposable
{
    public MigrationAppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-migrationlint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "Migrations"));

        WriteMigration("20260101100000_InitialCreate.cs", """
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace App.Migrations;

            public partial class InitialCreate : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.CreateTable(
                        name: "Orders",
                        columns: table => new
                        {
                            Id = table.Column<int>(nullable: false),
                            ShippingAddress = table.Column<string>(nullable: true)
                        });

                    migrationBuilder.CreateTable(
                        name: "OrderItems",
                        columns: table => new
                        {
                            Id = table.Column<int>(nullable: false)
                        });
                }
            }
            """);

        WriteMigration("20260301100000_AddDeliveryAddress.cs", """
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace App.Migrations;

            public partial class AddDeliveryAddress : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.AddColumn<string>(
                        name: "DeliveryAddress",
                        table: "Orders",
                        type: "nvarchar(200)",
                        nullable: true);
                }
            }
            """);

        WriteMigration("20260310100000_BackfillDeliveryAddress.cs", """
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace App.Migrations;

            public partial class BackfillDeliveryAddress : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql(
                        "UPDATE Orders SET DeliveryAddress = ShippingAddress WHERE Id BETWEEN 1 AND 500");
                }
            }
            """);

        WriteMigration("20260601100000_RemoveShippingAddress.cs", """
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace App.Migrations;

            public partial class RemoveShippingAddress : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropColumn(
                        name: "ShippingAddress",
                        table: "Orders");
                }
            }
            """);

        Write("migration-gates.json", """
            {
              "passes": [
                {
                  "object": "Orders.ShippingAddress",
                  "gate": "forward-compat",
                  "expandedAt": "2026-03-01",
                  "coldAt": "2026-05-25",
                  "consumers": [ "ShippingService", "LegacyExportJob" ]
                }
              ]
            }
            """);
    }

    public string Root { get; }

    public string GatesPath => Path.Combine(Root, "migration-gates.json");

    public void WriteMigration(string fileName, string content) =>
        Write(Path.Combine("Migrations", fileName), content);

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    public MigrationLintConfig Config(string? gatesOverride = null) =>
        MigrationLintConfig.Load(null, Root, gatesOverride is null ? null : Path.Combine(Root, gatesOverride));

    public MigrationLintReport Lint(string? gatesOverride = null)
    {
        var config = Config(gatesOverride);
        return MigrationLintAnalyzer.Analyze(config, MigrationParser.Parse(config.MigrationsFullPath));
    }

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
}
