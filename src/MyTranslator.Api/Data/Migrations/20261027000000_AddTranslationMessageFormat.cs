using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261027000000_AddTranslationMessageFormat")]
public sealed class AddTranslationMessageFormat : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("TranslationMessageFormat", "Models", type: "TEXT",
            maxLength: 40, nullable: false, defaultValue: "general");
        migrationBuilder.AddColumn<string>("TranslationMessageFormat", "TranslationRuns", type: "TEXT",
            maxLength: 40, nullable: false, defaultValue: "general");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 手写迁移没有旧 TargetModel；使用当前 SQLite 支持的原生删除列语句。
        migrationBuilder.Sql("ALTER TABLE Models DROP COLUMN TranslationMessageFormat;");
        migrationBuilder.Sql("ALTER TABLE TranslationRuns DROP COLUMN TranslationMessageFormat;");
    }
}
