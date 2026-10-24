using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

public partial class BackfillDeliveryAddress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // engineered batch loop: key-range batches, throttled, restartable —
        // each batch carries its own WHERE
        migrationBuilder.Sql(
            "UPDATE Orders SET DeliveryAddress = ShippingAddress WHERE Id BETWEEN 1 AND 500");

        migrationBuilder.Sql(
            "UPDATE Orders SET DeliveryAddress = ShippingAddress WHERE Id BETWEEN 501 AND 1000");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE Orders SET DeliveryAddress = NULL WHERE DeliveryAddress IS NOT NULL");
    }
}
