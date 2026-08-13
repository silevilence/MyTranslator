using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MyTranslator.Api.Data;
using MyTranslator.Api.FileTasks;

namespace MyTranslator.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _environment;
    private readonly string? _initialToken;
    private readonly HttpMessageHandler? _urlImportHandler;

    public ApiFactory() : this("Development", null)
    {
    }

    internal ApiFactory(
        string environment,
        string? initialToken,
        HttpMessageHandler? urlImportHandler = null)
    {
        _environment = environment;
        _initialToken = initialToken;
        _urlImportHandler = urlImportHandler;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                ["INITIAL_TOKEN"] = _initialToken
            });
        });
        builder.ConfigureServices(services =>
        {
            _connection.Open();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            if (_urlImportHandler is not null)
            {
                services.RemoveAll<UrlImportClient>();
                services.AddSingleton(new UrlImportClient(new HttpClient(_urlImportHandler, disposeHandler: false)));
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}
