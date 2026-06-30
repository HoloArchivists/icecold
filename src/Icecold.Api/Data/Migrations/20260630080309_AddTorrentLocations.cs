using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icecold.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTorrentLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "torrent_locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TorrentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ContentLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ContentLastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_torrent_locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_torrent_locations_torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO torrent_locations (
                    "Id",
                    "TorrentId",
                    "SourceName",
                    "SourcePath",
                    "ContentLength",
                    "ContentVersion",
                    "ContentLastModified",
                    "Status",
                    "IsPrimary",
                    "Priority",
                    "LastVerifiedAt",
                    "LastError",
                    "CreatedAt",
                    "UpdatedAt")
                SELECT
                    "Id",
                    COALESCE("DuplicateOfId", "Id"),
                    "SourceName",
                    "SourcePath",
                    "ContentLength",
                    "ContentVersion",
                    "ContentLastModified",
                    CASE WHEN "Status" = 'Failed' THEN 'Stale' ELSE 'Active' END,
                    "Status" = 'Ready',
                    CASE WHEN "Status" = 'Ready' THEN 0 ELSE 100 END,
                    CASE WHEN "Status" IN ('Ready', 'Duplicate') THEN "CompletedAt" ELSE NULL END,
                    CASE WHEN "Status" = 'Failed' THEN "Error" ELSE NULL END,
                    "CreatedAt",
                    "UpdatedAt"
                FROM torrents
                WHERE "SourceName" <> '' AND "SourcePath" <> '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_ContentIdentity",
                table: "torrent_locations",
                columns: new[] { "TorrentId", "SourceName", "SourcePath", "ContentLength", "ContentVersion" },
                unique: true,
                filter: "\"ContentVersion\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_Primary",
                table: "torrent_locations",
                column: "TorrentId",
                unique: true,
                filter: "\"IsPrimary\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_SourceName_SourcePath",
                table: "torrent_locations",
                columns: new[] { "SourceName", "SourcePath" });

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_Status",
                table: "torrent_locations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_TorrentId",
                table: "torrent_locations",
                column: "TorrentId");

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_TorrentId_IsPrimary",
                table: "torrent_locations",
                columns: new[] { "TorrentId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_torrent_locations_TorrentId_Priority",
                table: "torrent_locations",
                columns: new[] { "TorrentId", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "torrent_locations");
        }
    }
}
