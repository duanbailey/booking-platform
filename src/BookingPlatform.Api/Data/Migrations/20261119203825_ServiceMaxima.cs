using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookingPlatform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceMaxima : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Index order matters in both directions: the business_id foreign key needs an index at
            // every step, so the replacement is created before the old one is dropped (MySQL 1553).
            migrationBuilder.DropCheckConstraint(
                name: "ck_services_buffers",
                table: "services");

            migrationBuilder.DropCheckConstraint(
                name: "ck_services_duration",
                table: "services");

            migrationBuilder.CreateIndex(
                name: "IX_services_business_id_name_id",
                table: "services",
                columns: new[] { "business_id", "name", "id" });

            migrationBuilder.DropIndex(
                name: "IX_services_business_id",
                table: "services");

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_buffers",
                table: "services",
                sql: "buffer_before_minutes BETWEEN 0 AND 1440 AND buffer_after_minutes BETWEEN 0 AND 1440");

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_duration",
                table: "services",
                sql: "duration_minutes BETWEEN 1 AND 1440");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_services_buffers",
                table: "services");

            migrationBuilder.DropCheckConstraint(
                name: "ck_services_duration",
                table: "services");

            migrationBuilder.CreateIndex(
                name: "IX_services_business_id",
                table: "services",
                column: "business_id");

            migrationBuilder.DropIndex(
                name: "IX_services_business_id_name_id",
                table: "services");

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_buffers",
                table: "services",
                sql: "buffer_before_minutes >= 0 AND buffer_after_minutes >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_duration",
                table: "services",
                sql: "duration_minutes >= 1");
        }
    }
}
