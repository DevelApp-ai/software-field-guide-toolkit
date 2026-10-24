using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

public partial class RemoveShippingAddress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // the seeded violation: the drop comes first and a later statement in the
        // same migration still references the dropped column — contract before
        // expand is the destructive order (the archive copy needed the column
        // alive)
        migrationBuilder.DropColumn(
            name: "ShippingAddress",
            table: "Orders");

        migrationBuilder.Sql(
            "INSERT INTO OrderArchive (OrderId, ShippingLine) SELECT Id, ShippingAddress FROM Orders");
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
