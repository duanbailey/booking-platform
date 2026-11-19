using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookingPlatform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BusinessSettingMaxima : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_cancellation_window",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_max_advance",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_min_notice",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_slot_step",
                table: "businesses");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_cancellation_window",
                table: "businesses",
                sql: "cancellation_window_hours BETWEEN 0 AND 8760");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_max_advance",
                table: "businesses",
                sql: "max_advance_days BETWEEN 1 AND 3650");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_min_notice",
                table: "businesses",
                sql: "min_notice_minutes BETWEEN 0 AND 525600");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_slot_step",
                table: "businesses",
                sql: "slot_step_minutes BETWEEN 1 AND 1440");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_cancellation_window",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_max_advance",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_min_notice",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_slot_step",
                table: "businesses");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_cancellation_window",
                table: "businesses",
                sql: "cancellation_window_hours >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_max_advance",
                table: "businesses",
                sql: "max_advance_days >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_min_notice",
                table: "businesses",
                sql: "min_notice_minutes >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_slot_step",
                table: "businesses",
                sql: "slot_step_minutes >= 1");
        }
    }
}
