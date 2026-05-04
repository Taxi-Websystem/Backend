using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTripStatsAndRideDriverLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AverageRating",
                table: "UserProfiles",
                type: "decimal(3,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TripCount",
                table: "UserProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DriverProfileId",
                table: "Rides",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Rating",
                table: "Rides",
                type: "decimal(3,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rides_DriverProfileId",
                table: "Rides",
                column: "DriverProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Rides_UserProfiles_DriverProfileId",
                table: "Rides",
                column: "DriverProfileId",
                principalTable: "UserProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Rides_UserProfiles_DriverProfileId",
                table: "Rides");

            migrationBuilder.DropIndex(
                name: "IX_Rides_DriverProfileId",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "AverageRating",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "TripCount",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DriverProfileId",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Rides");
        }
    }
}
