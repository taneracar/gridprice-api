using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridPrice.Api.Data.Migrations;

/// <inheritdoc />
public partial class _20261123173638_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nodes",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                MarketCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nodes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "price_revisions",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Run = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                IntervalStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                IntervalMinutes = table.Column<int>(type: "int", nullable: false),
                Revision = table.Column<int>(type: "int", nullable: false),
                ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                Lmp = table.Column<decimal>(type: "decimal(12,5)", precision: 12, scale: 5, nullable: false),
                Energy = table.Column<decimal>(type: "decimal(12,5)", precision: 12, scale: 5, nullable: false),
                Congestion = table.Column<decimal>(type: "decimal(12,5)", precision: 12, scale: 5, nullable: false),
                Loss = table.Column<decimal>(type: "decimal(12,5)", precision: 12, scale: 5, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_price_revisions", x => x.Id);
                table.ForeignKey(
                    name: "FK_price_revisions_nodes_NodeId",
                    column: x => x.NodeId,
                    principalTable: "nodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_nodes_MarketCode",
            table: "nodes",
            column: "MarketCode");

        migrationBuilder.CreateIndex(
            name: "IX_price_revisions_NodeId_Run_IntervalStartUtc_Revision",
            table: "price_revisions",
            columns: new[] { "NodeId", "Run", "IntervalStartUtc", "Revision" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "price_revisions");

        migrationBuilder.DropTable(
            name: "nodes");
    }
}