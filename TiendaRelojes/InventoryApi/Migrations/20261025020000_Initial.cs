using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InventoryApi.Migrations;

[DbContext(typeof(InventoryDb))]
[Migration("20261025020000_Initial")]
public class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "inventory");

        migrationBuilder.CreateTable(
            name: "Products",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "text", nullable: false),
                Category = table.Column<string>(type: "text", nullable: false),
                Price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                Stock = table.Column<int>(type: "integer", nullable: false),
                Reserved = table.Column<int>(type: "integer", nullable: false),
                Image = table.Column<string>(type: "text", nullable: false),
                Description = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Products", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Reservations",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<int>(type: "integer", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Reservations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Movements",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ProductId = table.Column<int>(type: "integer", nullable: false),
                Change = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: false),
                At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Movements", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Processed",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EventId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Processed", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Processed_EventId",
            schema: "inventory",
            table: "Processed",
            column: "EventId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Reservations_OrderId_ProductId",
            schema: "inventory",
            table: "Reservations",
            columns: new[] { "OrderId", "ProductId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Products", schema: "inventory");
        migrationBuilder.DropTable(name: "Reservations", schema: "inventory");
        migrationBuilder.DropTable(name: "Movements", schema: "inventory");
        migrationBuilder.DropTable(name: "Processed", schema: "inventory");
        migrationBuilder.DropSchema(name: "inventory");
    }
}
