using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResidentPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResidentPlaylists",
                columns: table => new
                {
                    Resident = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AiPlaylistJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ManualPlaylistJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ManualUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ManualUpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResidentPlaylists", x => x.Resident);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResidentPlaylists");
        }
    }
}
