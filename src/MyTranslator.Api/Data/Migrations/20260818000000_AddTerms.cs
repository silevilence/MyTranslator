using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260818000000_AddTerms")]
public partial class AddTerms : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Terms",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceTerm = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                SourceTermKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                TargetTerm = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                SourceLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TargetLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                CaseSensitive = table.Column<bool>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Terms", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_Terms_SourceLanguage_TargetLanguage_SourceTermKey",
            table: "Terms",
            columns: new[] { "SourceLanguage", "TargetLanguage", "SourceTermKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Terms_UpdatedAt_Id",
            table: "Terms",
            columns: new[] { "UpdatedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Terms");
}
