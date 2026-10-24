using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

public partial class RemoveLegacyTotal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // the seeded violation: a contract step with no gate pass in the
        // ledger — the gate fails builds, not reviews
        migrationBuilder.DropColumn(
            name: "LegacyTotal",
            table: "Orders");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "LegacyTotal",
            table: "Orders",
            type: "decimal(18,2)",
            nullable: false);
    }
}
