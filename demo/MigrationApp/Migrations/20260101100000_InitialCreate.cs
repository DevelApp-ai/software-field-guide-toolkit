using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationApp.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Orders",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
                CustomerName = table.Column<string>(nullable: true),
                ShippingAddress = table.Column<string>(nullable: true),
                LegacyTotal = table.Column<decimal>(nullable: false)
            });

        migrationBuilder.CreateTable(
            name: "OrderItems",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
                OrderId = table.Column<int>(nullable: false),
                ProductSku = table.Column<string>(nullable: true),
                Qty = table.Column<int>(nullable: false)
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OrderItems");
        migrationBuilder.DropTable(name: "Orders");
    }
}
