using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CareHomeId",
                table: "ResidentPlaylists",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CareHomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareHomes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AzureAdObjectId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AzureAdEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CareHomeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserProfiles_CareHomes_CareHomeId",
                        column: x => x.CareHomeId,
                        principalTable: "CareHomes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ResidentAccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResidentKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GrantedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResidentAccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResidentAccessGrants_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CareHomes_Name",
                table: "CareHomes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResidentAccessGrants_UserProfileId_ResidentKey",
                table: "ResidentAccessGrants",
                columns: new[] { "UserProfileId", "ResidentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_AzureAdEmail",
                table: "UserProfiles",
                column: "AzureAdEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_AzureAdObjectId",
                table: "UserProfiles",
                column: "AzureAdObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_CareHomeId",
                table: "UserProfiles",
                column: "CareHomeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResidentAccessGrants");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "CareHomes");

            migrationBuilder.DropColumn(
                name: "CareHomeId",
                table: "ResidentPlaylists");
        }
    }
}
