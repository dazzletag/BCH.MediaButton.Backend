using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiHomeUserAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllCareHomes",
                table: "UserProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "UserCareHomeGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CareHomeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GrantedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCareHomeGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCareHomeGrants_CareHomes_CareHomeId",
                        column: x => x.CareHomeId,
                        principalTable: "CareHomes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserCareHomeGrants_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserCareHomeGrants_CareHomeId",
                table: "UserCareHomeGrants",
                column: "CareHomeId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCareHomeGrants_UserProfileId_CareHomeId",
                table: "UserCareHomeGrants",
                columns: new[] { "UserProfileId", "CareHomeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserCareHomeGrants");

            migrationBuilder.DropColumn(
                name: "AllCareHomes",
                table: "UserProfiles");
        }
    }
}
