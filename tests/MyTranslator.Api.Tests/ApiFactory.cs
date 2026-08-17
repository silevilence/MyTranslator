using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
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
    private readonly IChatClient? _translationProvider;
    private readonly HttpMessageHandler? _translationHandler;
    private readonly IInterceptor? _databaseInterceptor;
    private readonly IAiChatClientFactory? _chatClientFactory;
    private readonly bool _seedTranslationConfiguration;
    private readonly ILoggerProvider? _loggerProvider;

    public ApiFactory() : this("Development", null)
    {
    }

    internal ApiFactory(
        string environment,
        string? initialToken,
        HttpMessageHandler? urlImportHandler = null,
        IChatClient? translationProvider = null,
        HttpMessageHandler? translationHandler = null,
        string? connectionString = null,
        IInterceptor? databaseInterceptor = null,
        IAiChatClientFactory? chatClientFactory = null,
        bool seedTranslationConfiguration = true,
        ILoggerProvider? loggerProvider = null)
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
        _chatClientFactory = chatClientFactory;
        _seedTranslationConfiguration = seedTranslationConfiguration;
        _loggerProvider = loggerProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            if (_loggerProvider is not null)
            {
                logging.AddProvider(_loggerProvider);
            }
        });
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["INITIAL_TOKEN"] = _initialToken,
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
            if (_chatClientFactory is not null)
            {
                services.RemoveAll<IAiChatClientFactory>();
                services.AddSingleton(_chatClientFactory);
            }
            else if (_translationProvider is not null)
            {
                services.RemoveAll<IAiChatClientFactory>();
                services.AddSingleton<IAiChatClientFactory>(new TestChatClientFactory(_translationProvider));
            }
            else if (_translationHandler is not null)
            {
                services.AddHttpClient("AiChatClient")
                    .ConfigurePrimaryHttpMessageHandler(() => _translationHandler);
            }

            if (IsTranslationConfigured && _seedTranslationConfiguration)
            {
                services.AddHostedService<TestAiConfigurationSeeder>();
            }
        });
    }

    private bool IsTranslationConfigured =>
        _translationProvider is not null || _translationHandler is not null || _chatClientFactory is not null;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }

    private sealed class TestChatClientFactory(IChatClient chatClient) : IAiChatClientFactory
    {
        public IChatClient Create(AiProvider provider, AiModel model) => chatClient;
    }

    private sealed class TestAiConfigurationSeeder(IServiceScopeFactory scopeFactory) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await database.Providers.AnyAsync(cancellationToken))
            {
                return;
            }

            var providerId = Guid.NewGuid();
            database.Providers.Add(new AiProvider
            {
                Id = providerId,
                Name = "Test Provider",
                Kind = "openai",
                BaseUrl = "https://llm.test/v1",
                ApiKey = "test-key",
                Enabled = true,
                IsDefault = true,
                BatchSize = 20,
                RequestTimeout = TimeSpan.FromMinutes(1),
                MaxAttempts = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                Models =
                [
                    new AiModel
                    {
                        Id = Guid.NewGuid(),
                        ModelId = "test-model",
                        DisplayName = "Test Model",
                        IsDefault = true,
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
