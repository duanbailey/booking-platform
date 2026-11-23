using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookingPlatform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxRelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_published_at_created_at",
                table: "outbox");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "outbox",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "outbox",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "outbox",
                type: "datetime(6)",
                nullable: true);

            // Hand-written: MySQL needs an AUTO_INCREMENT column to be a key in the same statement, and the
            // provider only generates AUTO_INCREMENT for primary keys. Existing rows are numbered in table order
            // (decided 2026-11-23). Copy this SQL if migrations are ever squashed (T27).
            migrationBuilder.Sql("ALTER TABLE outbox ADD COLUMN seq bigint NOT NULL AUTO_INCREMENT, ADD UNIQUE INDEX IX_outbox_seq (seq);");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_group_id_published_at_seq",
                table: "outbox",
                columns: new[] { "message_group_id", "published_at", "seq" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_published_at_seq",
                table: "outbox",
                columns: new[] { "published_at", "seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_message_group_id_published_at_seq",
                table: "outbox");

            migrationBuilder.DropIndex(
                name: "IX_outbox_published_at_seq",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "outbox");

            // Dropping the column drops IX_outbox_seq with it (MySQL won't drop an AUTO_INCREMENT column's only key first).
            migrationBuilder.DropColumn(
                name: "seq",
                table: "outbox");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_published_at_created_at",
                table: "outbox",
                columns: new[] { "published_at", "created_at" });
        }
    }
}
