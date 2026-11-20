using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookingPlatform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookingsStaffStartIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_bookings_staff_id_start_utc",
                table: "bookings",
                columns: new[] { "staff_id", "start_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bookings_staff_id_start_utc",
                table: "bookings");
        }
    }
}
