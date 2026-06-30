using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icecold.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMseObfuscatedHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MseObfuscatedHashHex",
                table: "torrents",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_torrents_MseObfuscatedHashHex",
                table: "torrents",
                column: "MseObfuscatedHashHex",
                unique: true,
                filter: "\"MseObfuscatedHashHex\" IS NOT NULL AND \"Status\" = 'Ready'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_torrents_MseObfuscatedHashHex",
                table: "torrents");

            migrationBuilder.DropColumn(
                name: "MseObfuscatedHashHex",
                table: "torrents");
        }
    }
}
