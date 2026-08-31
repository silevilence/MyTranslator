using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260831000000_AddTranslationMemoryEntries")]
public partial class AddTranslationMemoryEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TranslationMemoryEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ContentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SourceText = table.Column<string>(type: "TEXT", nullable: false),
                TargetText = table.Column<string>(type: "TEXT", nullable: false),
                SourceLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TargetLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                MarkupTableJson = table.Column<string>(type: "TEXT", nullable: false),
                Origin = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                OriginTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                OriginSegmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                OriginExtractionRevision = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAtSortKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TranslationMemoryEntries", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_TranslationMemoryEntries_ContentKey",
            table: "TranslationMemoryEntries",
            column: "ContentKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TranslationMemoryEntries_SourceLanguage_TargetLanguage_CreatedAtSortKey_Id",
            table: "TranslationMemoryEntries",
            columns: new[] { "SourceLanguage", "TargetLanguage", "CreatedAtSortKey", "Id" },
            descending: new[] { false, false, true, false });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TranslationMemoryEntries");
    }
}
