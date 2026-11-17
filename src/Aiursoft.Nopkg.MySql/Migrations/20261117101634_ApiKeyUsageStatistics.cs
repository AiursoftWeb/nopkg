using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.Nopkg.MySql.Migrations
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
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPublishedPackage",
                table: "UserApiKeys",
                type: "varchar(214)",
                maxLength: 214,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LastPublishedVersion",
                table: "UserApiKeys",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "PublishedPackagesCount",
                table: "UserApiKeys",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TotalUses",
                table: "UserApiKeys",
                type: "bigint",
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
