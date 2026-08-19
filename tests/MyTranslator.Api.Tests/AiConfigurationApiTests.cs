using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTranslator.Api.Data;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Tests;

public sealed class AiConfigurationApiTests
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task ProviderAndModelCrudEnforcesDefaultsMasksKeysAndCascades()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var first = await CreateProviderAsync(
            client,
            "First",
            "openai",
            "https://first.test/v1",
            "sk-0123456789abcdef",
            isDefault: true);
        var second = await CreateProviderAsync(
            client,
            "Second",
            "ollama",
            "http://localhost:11434",
            null,
            isDefault: true);
        var firstId = first.GetProperty("id").GetGuid();
        var secondId = second.GetProperty("id").GetGuid();

        Assert.Equal("sk-***cdef", first.GetProperty("apiKeyMasked").GetString());
        Assert.False(first.TryGetProperty("apiKey", out _));

        var firstModel = await CreateModelAsync(client, firstId, "model-a", "Model A", isDefault: true);
        var secondModel = await CreateModelAsync(client, firstId, "model-b", "Model B", isDefault: true);
        var firstModelId = firstModel.GetProperty("id").GetGuid();
        var secondModelId = secondModel.GetProperty("id").GetGuid();

        var providersResponse = await client.GetAsync("/api/providers");
        var providersBody = await providersResponse.Content.ReadAsStringAsync();
        Assert.True(providersResponse.IsSuccessStatusCode, providersBody);
        var providers = JsonSerializer.Deserialize<JsonElement>(providersBody);
        var providerItems = providers.EnumerateArray().ToArray();
        Assert.Equal(2, providerItems.Length);
        Assert.False(providerItems.Single(item => item.GetProperty("id").GetGuid() == firstId)
            .GetProperty("isDefault").GetBoolean());
        Assert.True(providerItems.Single(item => item.GetProperty("id").GetGuid() == secondId)
            .GetProperty("isDefault").GetBoolean());
        var models = providerItems.Single(item => item.GetProperty("id").GetGuid() == firstId)
            .GetProperty("models").EnumerateArray().ToArray();
        Assert.False(models.Single(item => item.GetProperty("id").GetGuid() == firstModelId)
            .GetProperty("isDefault").GetBoolean());
        Assert.True(models.Single(item => item.GetProperty("id").GetGuid() == secondModelId)
            .GetProperty("isDefault").GetBoolean());

        var update = await client.PutAsJsonAsync($"/api/providers/{firstId}", new
        {
            name = "First Updated",
            kind = "openai",
            baseUrl = "https://first.test/v1",
            apiKey = first.GetProperty("apiKeyMasked").GetString(),
            enabled = true,
            isDefault = true,
            batchSize = 10,
            requestTimeout = "00:00:30",
            maxAttempts = 2
        });
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("sk-***cdef", updated.GetProperty("apiKeyMasked").GetString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("sk-0123456789abcdef", (await database.Providers.FindAsync(firstId))!.ApiKey);
        }

        var reservedField = await client.PutAsJsonAsync($"/api/providers/{firstId}", new
        {
            name = "Invalid",
            kind = "openai",
            apiKeyMasked = "***"
        });
        Assert.Equal(HttpStatusCode.BadRequest, reservedField.StatusCode);
        Assert.Equal(
            "invalid_provider_key_field",
            (await reservedField.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var malformedBody = await client.PostAsJsonAsync("/api/providers", "not-an-object");
        await AssertGenericBadRequestAsync(malformedBody);

        var duplicate = await client.PostAsJsonAsync($"/api/providers/{firstId}/models", new
        {
            modelId = "model-b",
            displayName = "Duplicate"
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(
            "model_id_conflict",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/providers/{firstId}")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await database.Providers.AnyAsync(item => item.Id == firstId));
            Assert.False(await database.Models.AnyAsync(item => item.ProviderId == firstId));
        }
    }

    [Fact]
    public async Task TranslationRunResolvesDefaultProviderDefaultModelAndExplicitPair()
    {
        var selectionFactory = new SelectionAwareChatClientFactory();
        using var factory = new ApiFactory(
            "Development",
            null,
            chatClientFactory: selectionFactory,
            seedTranslationConfiguration: false);
        using var client = CreateClient(factory);
        var defaultProvider = await CreateProviderAsync(
            client,
            "Default",
            "openai",
            "https://default.test/v1",
            "default-secret",
            isDefault: true);
        var explicitProvider = await CreateProviderAsync(
            client,
            "Explicit",
            "ollama",
            "http://ollama.test",
            null,
            isDefault: false);
        var defaultProviderId = defaultProvider.GetProperty("id").GetGuid();
        var explicitProviderId = explicitProvider.GetProperty("id").GetGuid();
        var defaultModel = await CreateModelAsync(
            client,
            defaultProviderId,
            "default-model",
            "Default Model",
            isDefault: true);
        var providerDefaultModel = await CreateModelAsync(
            client,
            explicitProviderId,
            "provider-default",
            "Provider Default",
            isDefault: true);
        var exactModel = await CreateModelAsync(
            client,
            explicitProviderId,
            "exact-model",
            "Exact Model",
            isDefault: false);
        var defaultModelId = defaultModel.GetProperty("id").GetGuid();
        var providerDefaultModelId = providerDefaultModel.GetProperty("id").GetGuid();
        var exactModelId = exactModel.GetProperty("id").GetGuid();

        var defaultTask = await ImportTextAsync(client, "Default text");
        var defaultRun = await CreateRunAsync(client, defaultTask, null, null);
        Assert.Equal(defaultProviderId, defaultRun.GetProperty("providerId").GetGuid());
        Assert.Equal(defaultModelId, defaultRun.GetProperty("modelId").GetGuid());
        await WaitForTerminalRunAsync(client, defaultTask, defaultRun.GetProperty("runId").GetGuid());

        var providerTask = await ImportTextAsync(client, "Provider text");
        var providerRun = await CreateRunAsync(client, providerTask, explicitProviderId, null);
        Assert.Equal(explicitProviderId, providerRun.GetProperty("providerId").GetGuid());
        Assert.Equal(providerDefaultModelId, providerRun.GetProperty("modelId").GetGuid());
        await WaitForTerminalRunAsync(client, providerTask, providerRun.GetProperty("runId").GetGuid());

        var exactTask = await ImportTextAsync(client, "Exact text");
        var exactRun = await CreateRunAsync(client, exactTask, explicitProviderId, exactModelId);
        Assert.Equal(explicitProviderId, exactRun.GetProperty("providerId").GetGuid());
        Assert.Equal(exactModelId, exactRun.GetProperty("modelId").GetGuid());
        await WaitForTerminalRunAsync(client, exactTask, exactRun.GetProperty("runId").GetGuid());

        Assert.Contains((defaultProviderId, defaultModelId), selectionFactory.Selections);
        Assert.Contains((explicitProviderId, providerDefaultModelId), selectionFactory.Selections);
        Assert.Contains((explicitProviderId, exactModelId), selectionFactory.Selections);
    }

    [Fact]
    public async Task InvalidSelectionsReturnContractCodesWithoutCreatingRuns()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            chatClientFactory: new SelectionAwareChatClientFactory(),
            seedTranslationConfiguration: false);
        using var client = CreateClient(factory);
        var enabledProvider = await CreateProviderAsync(
            client,
            "Enabled",
            "openai",
            "https://enabled.test/v1",
            "enabled-secret",
            isDefault: false);
        var disabledProvider = await CreateProviderAsync(
            client,
            "Disabled",
            "ollama",
            "http://disabled.test",
            null,
            isDefault: false,
            enabled: false);
        var enabledId = enabledProvider.GetProperty("id").GetGuid();
        var disabledId = disabledProvider.GetProperty("id").GetGuid();
        var enabledModel = await CreateModelAsync(client, enabledId, "enabled-model", "Enabled", true);
        await CreateModelAsync(client, disabledId, "disabled-model", "Disabled", true);

        var taskId = await ImportTextAsync(client, "Hello");
        var providerNotFound = await PostRunAsync(client, taskId, Guid.NewGuid(), null);
        Assert.Equal(HttpStatusCode.NotFound, providerNotFound.StatusCode);
        Assert.Equal("provider_not_found", await ReadCodeAsync(providerNotFound));

        var modelNotFound = await PostRunAsync(client, taskId, enabledId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, modelNotFound.StatusCode);
        Assert.Equal("model_not_found", await ReadCodeAsync(modelNotFound));

        var disabled = await PostRunAsync(client, taskId, disabledId, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, disabled.StatusCode);
        Assert.Equal("provider_disabled", await ReadCodeAsync(disabled));
        Assert.Equal(
            "created",
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}"))
            .GetProperty("status").GetString());

        var noDefault = await PostRunAsync(client, taskId, null, null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noDefault.StatusCode);
        Assert.Equal("llm_not_configured", await ReadCodeAsync(noDefault));
        Assert.Equal(
            "failed",
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}"))
            .GetProperty("status").GetString());
        Assert.Equal(
            0,
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs"))
            .GetProperty("items").GetArrayLength());

        Assert.NotEqual(Guid.Empty, enabledModel.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task DefaultDisabledAndProviderWithoutDefaultModelUseContractMappings()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            chatClientFactory: new SelectionAwareChatClientFactory(),
            seedTranslationConfiguration: false);
        using var client = CreateClient(factory);
        var disabledDefault = await CreateProviderAsync(
            client,
            "Disabled Default",
            "ollama",
            "http://disabled-default.test",
            null,
            isDefault: true,
            enabled: false);
        var providerWithoutDefault = await CreateProviderAsync(
            client,
            "No Default Model",
            "openai",
            "https://no-default.test/v1",
            "configured-secret",
            isDefault: false);
        await CreateModelAsync(
            client,
            disabledDefault.GetProperty("id").GetGuid(),
            "disabled-default-model",
            "Disabled Default Model",
            isDefault: true);

        var defaultTask = await ImportTextAsync(client, "Default selection");
        var disabledResponse = await PostRunAsync(client, defaultTask, null, null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledResponse.StatusCode);
        Assert.Equal("llm_not_configured", await ReadCodeAsync(disabledResponse));
        Assert.Equal(
            "failed",
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{defaultTask}"))
            .GetProperty("status").GetString());

        var explicitTask = await ImportTextAsync(client, "Explicit selection");
        var noDefaultModelResponse = await PostRunAsync(
            client,
            explicitTask,
            providerWithoutDefault.GetProperty("id").GetGuid(),
            null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDefaultModelResponse.StatusCode);
        Assert.Equal("model_not_found", await ReadCodeAsync(noDefaultModelResponse));
        Assert.Equal(
            "created",
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{explicitTask}"))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidSelectionShapesReturnContractBadRequests()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            chatClientFactory: new SelectionAwareChatClientFactory(),
            seedTranslationConfiguration: false);
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello");

        var malformedProvider = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                targetLanguage = "zh-CN",
                providerId = "not-a-uuid"
            });
        Assert.Equal(HttpStatusCode.BadRequest, malformedProvider.StatusCode);
        Assert.Equal("invalid_model_selection", await ReadCodeAsync(malformedProvider));

        var modelWithoutProvider = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                targetLanguage = "zh-CN",
                modelId = Guid.NewGuid()
            });
        Assert.Equal(HttpStatusCode.BadRequest, modelWithoutProvider.StatusCode);
        Assert.Equal("invalid_model_selection", await ReadCodeAsync(modelWithoutProvider));

        var malformedModel = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                targetLanguage = "zh-CN",
                providerId = Guid.NewGuid(),
                modelId = "not-a-uuid"
            });
        Assert.Equal(HttpStatusCode.BadRequest, malformedModel.StatusCode);
        Assert.Equal("invalid_model_selection", await ReadCodeAsync(malformedModel));
        Assert.Equal(
            "created",
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}"))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeletingConfigurationDuringRunFailsRemainingSegmentsAsModelUnavailable()
    {
        var chatClientFactory = new BlockingFirstBatchChatClientFactory();
        using var factory = new ApiFactory(
            "Development",
            null,
            chatClientFactory: chatClientFactory,
            seedTranslationConfiguration: false);
        using var client = CreateClient(factory);
        var provider = await CreateProviderAsync(
            client,
            "Transient",
            "openai",
            "https://transient.test/v1",
            "transient-secret",
            isDefault: true,
            batchSize: 1);
        var providerId = provider.GetProperty("id").GetGuid();
        await CreateModelAsync(client, providerId, "transient-model", "Transient Model", isDefault: true);
        var taskId = await ImportTextAsync(client, "One\n\nTwo");
        var created = await CreateRunAsync(client, taskId, null, null);

        try
        {
            await chatClientFactory.WaitForCallAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/providers/{providerId}")).StatusCode);
        }
        finally
        {
            chatClientFactory.Release();
        }

        var runId = created.GetProperty("runId").GetGuid();
        var run = await WaitForTerminalRunAsync(client, taskId, runId);
        var failures = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures");
        var failure = Assert.Single(failures.GetProperty("items").EnumerateArray());
        Assert.Equal("partial_failed", run.GetProperty("status").GetString());
        Assert.Equal("llm_model_unavailable", failure.GetProperty("code").GetString());
        Assert.False(failure.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, failure.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task ApiKeyIsMaskedAndNeverWrittenToApplicationLogs()
    {
        const string secret = "sk-super-secret-value-abcd";
        var logger = new CollectingLoggerProvider();
        using var factory = new ApiFactory("Development", null, loggerProvider: logger);
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "Secret Provider",
            kind = "openai",
            baseUrl = "https://secret.test/v1",
            apiKey = secret,
            enabled = true,
            isDefault = false
        });
        var body = await response.Content.ReadAsStringAsync();
        var listBody = await (await client.GetAsync("/api/providers")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, listBody, StringComparison.Ordinal);
        Assert.Contains("sk-***abcd", body, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
    }

    private static HttpClient CreateClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);
        return client;
    }

    private static async Task<JsonElement> CreateProviderAsync(
        HttpClient client,
        string name,
        string kind,
        string baseUrl,
        string? apiKey,
        bool isDefault,
        bool enabled = true,
        int batchSize = AiProviderRuntimeSettings.DefaultBatchSize)
    {
        var response = await client.PostAsJsonAsync("/api/providers", new
        {
            name,
            kind,
            baseUrl,
            apiKey,
            enabled,
            isDefault,
            batchSize,
            requestTimeout = "00:01:00",
            maxAttempts = 3
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CreateModelAsync(
        HttpClient client,
        Guid providerId,
        string modelId,
        string displayName,
        bool isDefault)
    {
        var response = await client.PostAsJsonAsync($"/api/providers/{providerId}/models", new
        {
            modelId,
            displayName,
            supportsThinking = false,
            supportsToolUse = false,
            supportsStreaming = true,
            isDefault
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> ImportTextAsync(HttpClient client, string text)
    {
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        request.Add(file, "file", "sample.txt");
        request.Add(new StringContent("txt"), "fileType");
        var response = await client.PostAsync("/api/tasks/imports/file", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();
    }

    private static async Task<JsonElement> CreateRunAsync(
        HttpClient client,
        Guid taskId,
        Guid? providerId,
        Guid? modelId)
    {
        var response = await PostRunAsync(client, taskId, providerId, modelId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PostRunAsync(
        HttpClient client,
        Guid taskId,
        Guid? providerId,
        Guid? modelId) => client.PostAsJsonAsync(
        $"/api/tasks/{taskId}/translation-runs",
        new
        {
            extractionRevision = 1,
            sourceLanguage = "en",
            targetLanguage = "zh-CN",
            providerId,
            modelId
        });

    private static async Task<JsonElement> WaitForTerminalRunAsync(
        HttpClient client,
        Guid taskId,
        Guid runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/tasks/{taskId}/translation-runs/{runId}");
            if (run.GetProperty("status").GetString() is "completed" or "partial_failed" or "failed")
            {
                return run;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The translation run did not reach a terminal state.");
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task AssertGenericBadRequestAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(problem.TryGetProperty("code", out _));
    }

    private sealed class SelectionAwareChatClientFactory : IAiChatClientFactory
    {
        public ConcurrentBag<(Guid ProviderId, Guid ModelId)> Selections { get; } = [];

        public IChatClient Create(AiProvider provider, AiModel model)
        {
            Selections.Add((provider.Id, model.Id));
            return new EchoChatClient($"{provider.Name}/{model.DisplayName}");
        }
    }

    private sealed class EchoChatClient(string prefix) : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TestTranslationOutput>>(
            request.Segments
                .Select(segment => new TestTranslationOutput(
                    segment.SegmentId,
                    $"{prefix}: {segment.SourceText}"))
                .ToArray());
    }

    private sealed class BlockingFirstBatchChatClientFactory : IAiChatClientFactory
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IChatClient Create(AiProvider provider, AiModel model) => new Client(this);

        public Task WaitForCallAsync(TimeSpan timeout) => _started.Task.WaitAsync(timeout);

        public void Release() => _released.TrySetResult();

        private sealed class Client(BlockingFirstBatchChatClientFactory owner) : TestTranslationChatClient
        {
            protected override async Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
                TestTranslationRequest request,
                CancellationToken cancellationToken)
            {
                owner._started.TrySetResult();
                await owner._released.Task.WaitAsync(cancellationToken);
                return request.Segments
                    .Select(segment => new TestTranslationOutput(segment.SegmentId, $"Translated: {segment.SourceText}"))
                    .ToArray();
            }
        }
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.ToString());
                }
            }
        }
    }
}
