using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookingPlatform.Api.Data.Migrations
{
    /// <summary>
    /// users.email must compare ignoring case but not accents (specs/05-data-model.md). The model
    /// and InitialSchema already ask for utf8mb4_0900_as_ci, but Oracle's MySQL EF provider leaves
    /// column collations out of the DDL, so the column got the table default (ai_ci). Hand-written
    /// SQL: copy it across when migrations are squashed (T27).
    /// </summary>
    public partial class FixEmailCollation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE users MODIFY email varchar(254) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_ci NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE users MODIFY email varchar(254) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL;");
        }
    }
}
