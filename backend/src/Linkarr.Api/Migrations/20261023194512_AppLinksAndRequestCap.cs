using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkarr.Api.Migrations
{
    /// <inheritdoc />
    public partial class AppLinksAndRequestCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestsPerDay",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            // Name-based linking could have tied one app user to two accounts: the oldest account keeps it, the other relinks.
            migrationBuilder.Sql("UPDATE Accounts SET VisularrId = NULL WHERE VisularrId IS NOT NULL AND Id NOT IN (SELECT MIN(Id) FROM Accounts WHERE VisularrId IS NOT NULL GROUP BY VisularrId);");
            migrationBuilder.Sql("UPDATE Accounts SET ModularrId = NULL WHERE ModularrId IS NOT NULL AND Id NOT IN (SELECT MIN(Id) FROM Accounts WHERE ModularrId IS NOT NULL GROUP BY ModularrId);");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ModularrId",
                table: "Accounts",
                column: "ModularrId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_VisularrId",
                table: "Accounts",
                column: "VisularrId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_ModularrId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_VisularrId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "RequestsPerDay",
                table: "Settings");
        }
    }
}
