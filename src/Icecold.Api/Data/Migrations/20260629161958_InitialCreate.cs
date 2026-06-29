using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icecold.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "torrents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ContentLastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InfoHashHex = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DuplicateOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    TorrentBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    PieceLength = table.Column<int>(type: "integer", nullable: true),
                    PieceCount = table.Column<int>(type: "integer", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_torrents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_torrents_torrents_DuplicateOfId",
                        column: x => x.DuplicateOfId,
                        principalTable: "torrents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_torrents_DuplicateOfId",
                table: "torrents",
                column: "DuplicateOfId");

            migrationBuilder.CreateIndex(
                name: "IX_torrents_InfoHashHex",
                table: "torrents",
                column: "InfoHashHex",
                unique: true,
                filter: "\"InfoHashHex\" IS NOT NULL AND \"Status\" = 'Ready'");

            migrationBuilder.CreateIndex(
                name: "IX_torrents_SourceName_SourcePath",
                table: "torrents",
                columns: new[] { "SourceName", "SourcePath" });

            migrationBuilder.CreateIndex(
                name: "IX_torrents_Status",
                table: "torrents",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "torrents");
        }
    }
}
