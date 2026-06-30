using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Icecold.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContentIdentityUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_torrents_ContentIdentity",
                table: "torrents",
                columns: new[] { "SourceName", "SourcePath", "ContentLength", "ContentVersion" },
                unique: true,
                filter: "\"ContentVersion\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_torrents_ContentIdentity",
                table: "torrents");
        }
    }
}
