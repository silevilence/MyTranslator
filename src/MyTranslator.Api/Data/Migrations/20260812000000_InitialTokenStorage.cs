using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260812000000_InitialTokenStorage")]
public partial class InitialTokenStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ApiTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TokenPrefix = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ApiTokens", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ApiTokens_TokenHash",
            table: "ApiTokens",
            column: "TokenHash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ApiTokens");
    }
}
