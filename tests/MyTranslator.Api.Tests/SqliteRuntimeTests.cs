using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MyTranslator.Api.Tests;

public sealed class SqliteRuntimeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoadedNativeLibraryIncludesAggregateOverflowFix()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = Assert.IsType<string>(await command.ExecuteScalarAsync());
        output.WriteLine($"实际加载的原生 SQLite 版本：{version}");
        // CVE-2025-6965 在 SQLite 3.50.2 修复；检查实际加载的库，避免只升级包名而仍携带旧原生库。
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2), $"SQLite {version} 缺少 CVE-2025-6965 修复。");
    }
}
