using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261026000000_AddTranslationExperience")]
public sealed class AddTranslationExperience : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ReasoningEffort", "TranslationRuns", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("ActivityJson", "TranslationRuns", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("SourceFileName", "TranslationSegments", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ReasoningEffort", "TranslationRuns");
        migrationBuilder.DropColumn("ActivityJson", "TranslationRuns");
        migrationBuilder.DropColumn("SourceFileName", "TranslationSegments");
    }
}
