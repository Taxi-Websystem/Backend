using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class RideRoutePointsAndCoords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Route",
                table: "Rides");

            migrationBuilder.AddColumn<bool>(
                name: "IsRouteOptimizationEnabled",
                table: "SystemSettings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "FromLatitude",
                table: "Rides",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FromLongitude",
                table: "Rides",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ToLatitude",
                table: "Rides",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ToLongitude",
                table: "Rides",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RideRoutePoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideRoutePoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RideRoutePoints_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "IsRouteOptimizationEnabled",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_RideRoutePoints_RideId_RecordedAt",
                table: "RideRoutePoints",
                columns: new[] { "RideId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RideRoutePoints");

            migrationBuilder.DropColumn(
                name: "IsRouteOptimizationEnabled",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "FromLatitude",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "FromLongitude",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "ToLatitude",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "ToLongitude",
                table: "Rides");

            migrationBuilder.AddColumn<string>(
                name: "Route",
                table: "Rides",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
