using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260819000000_OptimizeTermListIndex")]
public partial class OptimizeTermListIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Terms_UpdatedAt_Id",
            table: "Terms");

        migrationBuilder.AddColumn<string>(
            name: "UpdatedAtSortKey",
            table: "Terms",
            type: "TEXT",
            maxLength: 40,
            nullable: false,
            defaultValue: "");

        // EF Core SQLite stores DateTimeOffset with the same optional fractional
        // digits used by TermSortKey, so the original text is already canonical.
        migrationBuilder.Sql(
            "UPDATE \"Terms\" SET \"UpdatedAtSortKey\" = \"UpdatedAt\";");

        migrationBuilder.CreateIndex(
            name: "IX_Terms_UpdatedAtSortKey_Id",
            table: "Terms",
            columns: new[] { "UpdatedAtSortKey", "Id" },
            descending: new[] { true, false });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Terms_UpdatedAtSortKey_Id",
            table: "Terms");

        migrationBuilder.DropColumn(
            name: "UpdatedAtSortKey",
            table: "Terms");

        migrationBuilder.CreateIndex(
            name: "IX_Terms_UpdatedAt_Id",
            table: "Terms",
            columns: new[] { "UpdatedAt", "Id" });
    }
}
