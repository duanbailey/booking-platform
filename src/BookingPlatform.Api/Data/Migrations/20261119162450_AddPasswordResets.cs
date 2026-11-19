using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookingPlatform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "notifications",
                type: "enum('Invite','AccessGranted','PasswordReset','PasswordChanged','Confirmation','Rescheduled','Cancelled','LocalTimeChanged','RebookedInsideWindow','Reminder24h','Reminder1h')",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "enum('Invite','AccessGranted','Confirmation','Rescheduled','Cancelled','LocalTimeChanged','RebookedInsideWindow','Reminder24h','Reminder1h')");

            migrationBuilder.CreateTable(
                name: "password_resets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false),
                    user_id = table.Column<Guid>(type: "char(36)", nullable: false),
                    token_hash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    used_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_resets", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_resets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_password_resets_token_hash",
                table: "password_resets",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_resets_user_id_created_at",
                table: "password_resets",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_resets");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "notifications",
                type: "enum('Invite','AccessGranted','Confirmation','Rescheduled','Cancelled','LocalTimeChanged','RebookedInsideWindow','Reminder24h','Reminder1h')",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "enum('Invite','AccessGranted','PasswordReset','PasswordChanged','Confirmation','Rescheduled','Cancelled','LocalTimeChanged','RebookedInsideWindow','Reminder24h','Reminder1h')");
        }
    }
}
