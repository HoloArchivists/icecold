using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icecold.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDbBackedIndexingQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvailableAt",
                table: "torrents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "HashedBytes",
                table: "torrents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "torrents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "torrents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "torrents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing rows queue in submission order.
            migrationBuilder.Sql("""UPDATE torrents SET "AvailableAt" = "CreatedAt";""");

            migrationBuilder.CreateIndex(
                name: "IX_torrents_HashingLeases",
                table: "torrents",
                column: "LeaseExpiresAt",
                filter: "\"Status\" = 'Hashing'");

            migrationBuilder.CreateIndex(
                name: "IX_torrents_PendingQueue",
                table: "torrents",
                columns: new[] { "Priority", "AvailableAt", "Id" },
                descending: new[] { true, false, false },
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_torrents_HashingLeases",
                table: "torrents");

            migrationBuilder.DropIndex(
                name: "IX_torrents_PendingQueue",
                table: "torrents");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                table: "torrents");

            migrationBuilder.DropColumn(
                name: "HashedBytes",
                table: "torrents");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "torrents");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "torrents");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "torrents");
        }
    }
}
