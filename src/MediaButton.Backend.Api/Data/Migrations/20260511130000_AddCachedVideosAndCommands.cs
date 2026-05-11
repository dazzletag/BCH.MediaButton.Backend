using System;
using MediaButtonBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260511130000_AddCachedVideosAndCommands")]
    public partial class AddCachedVideosAndCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedVideos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Resident = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Term = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FilesizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DurationSeconds = table.Column<int>(type: "int", nullable: true),
                    DownloadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastPlayedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlayCount = table.Column<int>(type: "int", nullable: false),
                    Protected = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedVideos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedVideos_DeviceId_Source_SourceId",
                table: "CachedVideos",
                columns: new[] { "DeviceId", "Source", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedVideos_Resident",
                table: "CachedVideos",
                column: "Resident");

            migrationBuilder.CreateIndex(
                name: "IX_CachedVideos_LastSeenAt",
                table: "CachedVideos",
                column: "LastSeenAt");

            migrationBuilder.CreateTable(
                name: "DeviceCacheCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCacheCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCacheCommands_DeviceId_Status",
                table: "DeviceCacheCommands",
                columns: new[] { "DeviceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DeviceCacheCommands");
            migrationBuilder.DropTable(name: "CachedVideos");
        }
    }
}
