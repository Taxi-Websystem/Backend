using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations
{
    /// <inheritdoc />
    public partial class UpdateSeedPhone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "UserWhitelists",
                keyColumn: "Id",
                keyValue: 1,
                column: "PhoneNumber",
                value: "+380967515075");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "UserWhitelists",
                keyColumn: "Id",
                keyValue: 1,
                column: "PhoneNumber",
                value: "+380000000000");
        }
    }
}
