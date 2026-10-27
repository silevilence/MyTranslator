using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class SpecializedTranslationConfigurationTests
{
    [Theory]
    [InlineData("general")]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task FormatRoundTripsThroughModelApis(string format)
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, format);
        var providers = await client.GetFromJsonAsync<JsonElement>("/api/providers");
        Assert.Equal(format, providers[0].GetProperty("models")[0].GetProperty("translationMessageFormat").GetString());
        var update = await client.PutAsJsonAsync($"/api/providers/{providerId}/models/{modelId}",
            new { modelId = "test", displayName = "Test", translationMessageFormat = format, isDefault = true });
        update.EnsureSuccessStatusCode();
        var models = await client.GetFromJsonAsync<JsonElement>($"/api/providers/{providerId}/models");
        Assert.Equal(format, models[0].GetProperty("translationMessageFormat").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"\"")]
    [InlineData("\"HY-MT2\"")]
    [InlineData("\"unknown\"")]
    public async Task InvalidFormatsAreRejectedOnCreateAndUpdate(string json)
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, "general");
        var body = "{\"modelId\":\"other\",\"displayName\":\"Other\",\"translationMessageFormat\":" + json + "}";
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, $"/api/providers/{providerId}/models" +
                (method == HttpMethod.Put ? $"/{modelId}" : ""))
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_translation_message_format", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OmittedFormatAndMigratedModelsDefaultToGeneral()
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, "hy-mt2");
        var response = await client.PutAsJsonAsync($"/api/providers/{providerId}/models/{modelId}",
            new { modelId = "test", displayName = "Test", isDefault = true });
        response.EnsureSuccessStatusCode();
        Assert.Equal("general", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("translationMessageFormat").GetString());
        var taskId = await SpecializedTestApi.Import(client, "Hello");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.TranslationRuns.Add(new TranslationRun
        {
            Id = Guid.NewGuid(), TaskId = taskId, ExtractionRevision = 1, TargetLanguage = "zh-CN",
            ProviderId = providerId, ModelId = modelId, CreatedAt = DateTimeOffset.UtcNow,
            Status = TranslationRunStatus.Completed
        });
        await database.SaveChangesAsync();
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261026000000_AddTranslationExperience");
        await migrator.MigrateAsync();
        database.ChangeTracker.Clear();
        Assert.Equal("general", (await database.Models.SingleAsync()).TranslationMessageFormat);
        Assert.Equal("general", (await database.TranslationRuns.SingleAsync()).TranslationMessageFormat);
        Assert.False(database.Database.HasPendingModelChanges());
    }
}

internal static class SpecializedTestApi
{
    public static HttpClient Client(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-dev-00000000000000000000000000000000");
        return client;
    }

    public static async Task<(Guid ProviderId, Guid ModelId)> Configure(HttpClient client, string format, string kind = "openai", int maxAttempts = 2)
    {
        var response = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "Specialized test", kind, baseUrl = "http://model.test/v1", apiKey = "test-key",
            enabled = true, isDefault = true, batchSize = 20, maxAttempts
        });
        response.EnsureSuccessStatusCode();
        var providerId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        response = await client.PostAsJsonAsync($"/api/providers/{providerId}/models", new
        { modelId = "test", displayName = "Test", translationMessageFormat = format, isDefault = true });
        response.EnsureSuccessStatusCode();
        return (providerId, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    public static async Task<Guid> Import(HttpClient client, string source, string fileName = "sample.txt")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(source)), "file", fileName);
        var response = await client.PostAsync("/api/tasks/imports/file", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();
    }

    public static async Task<JsonElement> Wait(HttpClient client, Guid taskId, Guid runId, string resource = "translation-runs")
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/{resource}/{runId}");
            if (run.GetProperty("status").GetString() is "completed" or "partial_failed" or "failed") return run;
            await Task.Delay(20);
        }
        throw new TimeoutException("Run did not finish.");
    }
}
