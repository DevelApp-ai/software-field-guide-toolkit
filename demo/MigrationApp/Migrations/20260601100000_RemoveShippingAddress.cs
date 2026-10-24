using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

public partial class RemoveShippingAddress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // contract step, deliberately late: the ledger pass records the window
        // (expanded 2026-03-01, cold 2026-05-25) and the gate the consumers
        // passed — this migration is its own PR and contains nothing else
        migrationBuilder.DropColumn(
            name: "ShippingAddress",
            table: "Orders");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShippingAddress",
            table: "Orders",
            type: "nvarchar(200)",
            nullable: true);
    }
}
