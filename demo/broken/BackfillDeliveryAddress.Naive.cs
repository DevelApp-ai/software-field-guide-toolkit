using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

public partial class BackfillDeliveryAddress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // the seeded violation: one unbounded UPDATE, no WHERE — fails in
        // production via lock escalation, WAL flooding, replication lag
        migrationBuilder.Sql(
            "UPDATE Orders SET DeliveryAddress = ShippingAddress");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE Orders SET DeliveryAddress = NULL WHERE DeliveryAddress IS NOT NULL");
    }
}
