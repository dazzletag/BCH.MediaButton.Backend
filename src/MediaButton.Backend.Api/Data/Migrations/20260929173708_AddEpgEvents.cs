using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpgEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CareHomeId",
                table: "Devices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EpgEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CareHomeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChannelNumber = table.Column<int>(type: "int", nullable: true),
                    ChannelUuid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Subtitle = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StopUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SeriesCrid = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    EpisodeCrid = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Genre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsHd = table.Column<bool>(type: "bit", nullable: false),
                    IsSubtitled = table.Column<bool>(type: "bit", nullable: false),
                    IsAudioDescribed = table.Column<bool>(type: "bit", nullable: false),
                    SourceDeviceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpgEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpgEvents_CareHomeId_ChannelName_StartUtc",
                table: "EpgEvents",
                columns: new[] { "CareHomeId", "ChannelName", "StartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpgEvents_CareHomeId_SeriesCrid",
                table: "EpgEvents",
                columns: new[] { "CareHomeId", "SeriesCrid" });

            migrationBuilder.CreateIndex(
                name: "IX_EpgEvents_CareHomeId_StartUtc",
                table: "EpgEvents",
                columns: new[] { "CareHomeId", "StartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpgEvents");

            migrationBuilder.DropColumn(
                name: "CareHomeId",
                table: "Devices");
        }
    }
}
