using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260817000000_AddAiProvidersAndModels")]
public partial class AddAiProvidersAndModels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ModelId",
            table: "TranslationRuns",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ProviderId",
            table: "TranslationRuns",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "Providers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                BaseUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                BatchSize = table.Column<int>(type: "INTEGER", nullable: false),
                RequestTimeout = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Providers", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Models",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                ModelId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                SupportsThinking = table.Column<bool>(type: "INTEGER", nullable: false),
                SupportsToolUse = table.Column<bool>(type: "INTEGER", nullable: false),
                SupportsStreaming = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Models", x => x.Id);
                table.ForeignKey(
                    name: "FK_Models_Providers_ProviderId",
                    column: x => x.ProviderId,
                    principalTable: "Providers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Providers_CreatedAt",
            table: "Providers",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Providers_IsDefault",
            table: "Providers",
            column: "IsDefault",
            unique: true,
            filter: "\"IsDefault\" = 1");

        migrationBuilder.CreateIndex(
            name: "IX_Models_CreatedAt",
            table: "Models",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Models_ProviderId",
            table: "Models",
            column: "ProviderId",
            unique: true,
            filter: "\"IsDefault\" = 1");

        migrationBuilder.CreateIndex(
            name: "IX_Models_ProviderId_ModelId",
            table: "Models",
            columns: new[] { "ProviderId", "ModelId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Models");
        migrationBuilder.DropTable(name: "Providers");
        migrationBuilder.DropColumn(name: "ModelId", table: "TranslationRuns");
        migrationBuilder.DropColumn(name: "ProviderId", table: "TranslationRuns");
    }
}
