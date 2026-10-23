using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkarr.Api.Migrations
{
    /// <inheritdoc />
    public partial class StatsAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastIp",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AvailableSince",
                table: "Catalog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSignIn",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Snapshots",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Movies = table.Column<int>(type: "INTEGER", nullable: false),
                    Series = table.Column<int>(type: "INTEGER", nullable: false),
                    Available = table.Column<int>(type: "INTEGER", nullable: false),
                    Downloading = table.Column<int>(type: "INTEGER", nullable: false),
                    NotIndexed = table.Column<int>(type: "INTEGER", nullable: false),
                    Untracked = table.Column<int>(type: "INTEGER", nullable: false),
                    Missing = table.Column<int>(type: "INTEGER", nullable: false),
                    Unmonitored = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snapshots", x => x.Date);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Snapshots");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LastIp",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AvailableSince",
                table: "Catalog");

            migrationBuilder.DropColumn(
                name: "LastSignIn",
                table: "Accounts");
        }
    }
}
