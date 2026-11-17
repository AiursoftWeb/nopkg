using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.Nopkg.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyUsageStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastPublishedAt",
                table: "UserApiKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPublishedPackage",
                table: "UserApiKeys",
                type: "TEXT",
                maxLength: 214,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPublishedVersion",
                table: "UserApiKeys",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PublishedPackagesCount",
                table: "UserApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TotalUses",
                table: "UserApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPublishedAt",
                table: "UserApiKeys");

            migrationBuilder.DropColumn(
                name: "LastPublishedPackage",
                table: "UserApiKeys");

            migrationBuilder.DropColumn(
                name: "LastPublishedVersion",
                table: "UserApiKeys");

            migrationBuilder.DropColumn(
                name: "PublishedPackagesCount",
                table: "UserApiKeys");

            migrationBuilder.DropColumn(
                name: "TotalUses",
                table: "UserApiKeys");
        }
    }
}
