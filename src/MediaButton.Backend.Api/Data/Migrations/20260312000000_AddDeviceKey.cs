using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MediaButtonBackend.Data;

#nullable disable

namespace MediaButton.Backend.Api.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260312000000_AddDeviceKey")]
    public partial class AddDeviceKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceKey",
                table: "Devices",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceKey",
                table: "Devices");
        }
    }
}
