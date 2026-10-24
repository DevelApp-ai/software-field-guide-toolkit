using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DeliveryAddress", table: "Orders");
    }
}
