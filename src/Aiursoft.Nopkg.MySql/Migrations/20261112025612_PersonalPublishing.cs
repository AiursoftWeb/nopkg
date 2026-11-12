using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.Nopkg.MySql.Migrations
{
    /// <inheritdoc />
    public partial class PersonalPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackageFamilies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(214)", maxLength: 214, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OwnerUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsLocallyPublished = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Revision = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TagsJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageFamilies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageFamilies_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KeyHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KeyPrefix = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserApiKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PublishedPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FamilyId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ManifestJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UploadedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PublishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishedPackages_AspNetUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PublishedPackages_PackageFamilies_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "PackageFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PackageFamilies_Name",
                table: "PackageFamilies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageFamilies_OwnerUserId",
                table: "PackageFamilies",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishedPackages_FamilyId_Version",
                table: "PublishedPackages",
                columns: new[] { "FamilyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishedPackages_UploadedByUserId",
                table: "PublishedPackages",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserApiKeys_KeyHash",
                table: "UserApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserApiKeys_UserId",
                table: "UserApiKeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublishedPackages");

            migrationBuilder.DropTable(
                name: "UserApiKeys");

            migrationBuilder.DropTable(
                name: "PackageFamilies");
        }
    }
}
