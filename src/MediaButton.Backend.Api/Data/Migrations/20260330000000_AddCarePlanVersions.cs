using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCarePlanVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarePlanVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResidentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Section = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AssessmentDataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CareActionsDataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SignOffJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarePlanVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarePlanVersions_CarePlanId_VersionNumber",
                table: "CarePlanVersions",
                columns: new[] { "CarePlanId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CarePlanVersions_ResidentId_Section_IsCurrent",
                table: "CarePlanVersions",
                columns: new[] { "ResidentId", "Section", "IsCurrent" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CarePlanVersions");
        }
    }
}
