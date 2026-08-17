using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;
using MyTranslator.Api.FileTasks;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly string _environment;
    private readonly string? _initialToken;
    private readonly HttpMessageHandler? _urlImportHandler;
    private readonly ITranslationProvider? _translationProvider;
    private readonly HttpMessageHandler? _translationHandler;
    private readonly IInterceptor? _databaseInterceptor;

    public ApiFactory() : this("Development", null)
    {
    }

    internal ApiFactory(
        string environment,
        string? initialToken,
        HttpMessageHandler? urlImportHandler = null,
        ITranslationProvider? translationProvider = null,
        HttpMessageHandler? translationHandler = null,
        string? connectionString = null,
        IInterceptor? databaseInterceptor = null)
    {
        _connectionString = connectionString ??
            $"Data Source=MyTranslatorTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(_connectionString);
        _environment = environment;
        _initialToken = initialToken;
        _urlImportHandler = urlImportHandler;
        _translationProvider = translationProvider;
        _translationHandler = translationHandler;
        _databaseInterceptor = databaseInterceptor;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["INITIAL_TOKEN"] = _initialToken,
                ["Translation:Provider"] = _translationProvider?.Name ??
                                             (_translationHandler is null ? null : "openai-compatible"),
                ["Translation:BaseUrl"] = IsTranslationConfigured ? "https://llm.test" : null,
                ["Translation:ApiKey"] = IsTranslationConfigured ? "test-key" : null,
                ["Translation:Model"] = IsTranslationConfigured ? "test-model" : null,
                ["Translation:BatchSize"] = "20",
                ["Translation:MaxAttempts"] = "3",
                ["Translation:MaxConcurrentRuns"] = "4"
            });
        });
        builder.ConfigureServices(services =>
        {
            _connection.Open();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite(_connectionString);
                if (_databaseInterceptor is not null)
                {
                    options.AddInterceptors(_databaseInterceptor);
                }
            });
            if (_urlImportHandler is not null)
            {
                services.RemoveAll<UrlImportClient>();
                services.AddSingleton(new UrlImportClient(new HttpClient(_urlImportHandler, disposeHandler: false)));
            }
            if (_translationProvider is not null)
            {
                services.RemoveAll<ITranslationProvider>();
                services.AddSingleton(_translationProvider);
            }
            else if (_translationHandler is not null)
            {
                services.RemoveAll<ITranslationProvider>();
                services.AddSingleton<ITranslationProvider>(serviceProvider =>
                    new OpenAiCompatibleTranslationProvider(
                        new HttpClient(_translationHandler, disposeHandler: false),
                        serviceProvider.GetRequiredService<IOptions<TranslationOptions>>()));
            }
        });
    }

    private bool IsTranslationConfigured =>
        _translationProvider is not null || _translationHandler is not null;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}
