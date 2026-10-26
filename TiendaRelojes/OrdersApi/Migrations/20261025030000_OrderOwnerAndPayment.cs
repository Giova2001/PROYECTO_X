using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrdersApi.Migrations;

/// <summary>
/// Asocia el pedido al usuario que inició sesión y guarda el
/// detalle seguro del pago con tarjeta. Los pedidos existentes
/// quedan sin dueño (UserId nulo) y siguen visibles para el admin.
/// </summary>
[DbContext(typeof(OrdersDb))]
[Migration("20261025030000_OrderOwnerAndPayment")]
public class OrderOwnerAndPayment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "UserId",
            schema: "orders",
            table: "Orders",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Email",
            schema: "orders",
            table: "Orders",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "PaymentDetail",
            schema: "orders",
            table: "Orders",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_UserId",
            schema: "orders",
            table: "Orders",
            column: "UserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Orders_UserId", schema: "orders", table: "Orders");
        migrationBuilder.DropColumn(name: "UserId", schema: "orders", table: "Orders");
        migrationBuilder.DropColumn(name: "Email", schema: "orders", table: "Orders");
        migrationBuilder.DropColumn(name: "PaymentDetail", schema: "orders", table: "Orders");
    }
}
