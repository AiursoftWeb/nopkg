using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.Nopkg.Sqlite.Migrations
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
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 214, nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: true),
                    IsLocallyPublished = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "UserApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    KeyPrefix = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                });

            migrationBuilder.CreateTable(
                name: "PublishedPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FamilyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    UploadedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                });

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
