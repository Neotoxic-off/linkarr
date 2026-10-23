using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // Generated seed data

namespace Linkarr.Api.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndGroups : Migration
    {
        private const string NotMember = "IsAdmin = 0 AND NOT (Watch = 1 AND Request = 1 AND ManageDownloads = 0 AND ModularrAccess = 0 AND AllLibraries = 1)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    BuiltIn = table.Column<string>(type: "TEXT", nullable: true),
                    Admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    Watch = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllLibraries = table.Column<bool>(type: "INTEGER", nullable: false),
                    Libraries = table.Column<string>(type: "TEXT", nullable: false),
                    Request = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManageDownloads = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModularrAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestsPerDay = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxParentalRating = table.Column<int>(type: "INTEGER", nullable: true),
                    BlockUnrated = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxActiveSessions = table.Column<int>(type: "INTEGER", nullable: false),
                    Transcoding = table.Column<bool>(type: "INTEGER", nullable: false),
                    BitrateLimit = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    AllLibraries = table.Column<bool>(type: "INTEGER", nullable: false),
                    Libraries = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultRoleId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Groups_Roles_DefaultRoleId",
                        column: x => x.DefaultRoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.AccountId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_GroupMembers_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "Admin", "AllLibraries", "BitrateLimit", "BlockUnrated", "BuiltIn", "CanDownload", "Libraries", "ManageDownloads", "MaxActiveSessions", "MaxParentalRating", "ModularrAccess", "Name", "RemoteAccess", "Request", "RequestsPerDay", "Transcoding", "Watch" },
                values: new object[,]
                {
                    { 1, true, true, 0, false, "admin", true, "[]", true, 0, null, true, "Admin", true, true, null, true, true },
                    { 2, false, true, 0, false, "member", true, "[]", false, 0, null, false, "Member", true, true, null, true, true },
                    { 3, false, true, 0, true, "kid", true, "[]", false, 0, 10, false, "Kid", true, false, null, true, true },
                    { 4, false, false, 0, false, "guest", false, "[]", false, 0, null, false, "Guest", true, false, null, true, true }
                });

            // Every account gets a role: admins Admin; the others Member when that is exactly what they had, else a "Custom: <user>"
            // role of their current rights, so nothing changes for them. A library list becomes a group of its own.
            migrationBuilder.AddColumn<int>(
                name: "RoleId",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.Sql("UPDATE Accounts SET RoleId = 1 WHERE IsAdmin = 1;");
            migrationBuilder.Sql($"""
                INSERT INTO Roles (Name, BuiltIn, Admin, Watch, AllLibraries, Libraries, Request, ManageDownloads, ModularrAccess, RequestsPerDay,
                    MaxParentalRating, BlockUnrated, CanDownload, RemoteAccess, MaxActiveSessions, Transcoding, BitrateLimit)
                SELECT 'Custom: ' || Username, NULL, 0, Watch, AllLibraries, '[]', Request, ManageDownloads, ModularrAccess, NULL, NULL, 0, 1, 1, 0, 1, 0
                FROM Accounts WHERE {NotMember};
                """);
            migrationBuilder.Sql($"UPDATE Accounts SET RoleId = (SELECT r.Id FROM Roles r WHERE r.Name = 'Custom: ' || Accounts.Username) WHERE {NotMember};");
            migrationBuilder.Sql("""
                INSERT INTO Groups (Name, AllLibraries, Libraries, DefaultRoleId)
                SELECT 'Libraries: ' || Username, 0, Libraries, NULL FROM Accounts WHERE IsAdmin = 0 AND AllLibraries = 0;
                """);
            migrationBuilder.Sql("""
                INSERT INTO GroupMembers (AccountId, GroupId)
                SELECT a.Id, g.Id FROM Accounts a JOIN Groups g ON g.Name = 'Libraries: ' || a.Username WHERE a.IsAdmin = 0 AND a.AllLibraries = 0;
                """);

            foreach (string column in (string[])["IsAdmin", "Watch", "AllLibraries", "Libraries", "Request", "ManageDownloads", "ModularrAccess"])
            {
                migrationBuilder.DropColumn(name: column, table: "Accounts");
            }

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_RoleId",
                table: "Accounts",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_GroupId",
                table: "GroupMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_DefaultRoleId",
                table: "Groups",
                column: "DefaultRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name",
                table: "Groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Roles_RoleId",
                table: "Accounts",
                column: "RoleId",
                principalTable: "Roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "IsAdmin", table: "Accounts", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "Watch", table: "Accounts", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "AllLibraries", table: "Accounts", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<string>(name: "Libraries", table: "Accounts", type: "TEXT", nullable: false, defaultValue: "[]");
            migrationBuilder.AddColumn<bool>(name: "Request", table: "Accounts", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "ManageDownloads", table: "Accounts", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "ModularrAccess", table: "Accounts", type: "INTEGER", nullable: false, defaultValue: false);

            // The role's rights come back per account; group libraries are lost (only the role's own list is kept).
            migrationBuilder.Sql("""
                UPDATE Accounts SET (IsAdmin, Watch, AllLibraries, Libraries, Request, ManageDownloads, ModularrAccess) =
                    (SELECT r.Admin, r.Watch, r.AllLibraries, r.Libraries, r.Request, r.ManageDownloads, r.ModularrAccess FROM Roles r WHERE r.Id = Accounts.RoleId);
                """);

            migrationBuilder.DropForeignKey(name: "FK_Accounts_Roles_RoleId", table: "Accounts");
            migrationBuilder.DropIndex(name: "IX_Accounts_RoleId", table: "Accounts");
            migrationBuilder.DropColumn(name: "RoleId", table: "Accounts");
            migrationBuilder.DropTable(name: "GroupMembers");
            migrationBuilder.DropTable(name: "Groups");
            migrationBuilder.DropTable(name: "Roles");
        }
    }
}
