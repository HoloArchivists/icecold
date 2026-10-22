using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icecold.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiFileTorrents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMultiFile",
                table: "torrents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "torrent_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TorrentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    TorrentPath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ContentLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ContentLastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_torrent_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_torrent_files_torrents_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_torrent_files_TorrentId_Index",
                table: "torrent_files",
                columns: new[] { "TorrentId", "Index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "torrent_files");

            migrationBuilder.DropColumn(
                name: "IsMultiFile",
                table: "torrents");
        }
    }
}
