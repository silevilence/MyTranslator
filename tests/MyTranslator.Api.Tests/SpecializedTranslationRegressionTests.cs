using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;
using MyTranslator.Api.TaskOperations;
using MyTranslator.Api.Translation;
using static MyTranslator.Api.Tests.SpecializedTranslationExecutionTests;

namespace MyTranslator.Api.Tests;

public sealed class SpecializedTranslationRegressionTests
{
    [Theory]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task CancellationAndRecoveryPreserveCommittedSegmentsAndFormat(string format)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CaptureHandler("openai", (_, _) => "译文")
        {
            BeforeResponse = async call =>
            {
                if (call != 2) return;
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, cancellation.Token);
            }
        };
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format);
        var taskId = await SpecializedTestApi.Import(client, "Hello\n\nGoodbye");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new TranslationRunService(database, new TranslationRunQueue(), scope.ServiceProvider.GetRequiredService<TaskOperationLock>());
        var run = await service.CreateAsync(taskId, new CreateTranslationRunRequest(1, "en", "zh-CN", null, null), default);
        var processing = scope.ServiceProvider.GetRequiredService<TranslationRunProcessor>().ProcessAsync(run.RunId, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        using var recovery = new TranslationRunWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new TranslationRunQueue(), Options.Create(new TranslationOptions()), NullLogger<TranslationRunWorker>.Instance);
        await recovery.StartAsync(default);
        await recovery.StopAsync(default);
        database.ChangeTracker.Clear();
        var recovered = await database.TranslationRuns.SingleAsync();
        Assert.Equal(TranslationRunStatus.Failed, recovered.Status);
        Assert.Equal("translation_interrupted", recovered.FailureCode);
        Assert.Equal(format, recovered.TranslationMessageFormat);
        Assert.Null(recovered.ActiveTaskLockId);
        Assert.Equal(1, recovered.SucceededSegments);
        var segments = await database.TranslationSegments.OrderBy(segment => segment.Order).ToListAsync();
        Assert.Equal("译文", segments[0].TargetText);
        Assert.Null(segments[1].TargetText);
    }

    [Theory]
    [InlineData("hy-mt2", "openai")]
    [InlineData("hy-mt2", "ollama")]
    [InlineData("bilibili-index-translate", "openai")]
    [InlineData("bilibili-index-translate", "ollama")]
    public async Task MixedFilesAndMultilineSubtitlesKeepTheirStructure(string format, string kind)
    {
        var handler = new CaptureHandler(kind, (_, prompt) =>
        {
            var source = format == "hy-mt2" ? prompt.Split("不要额外解释：\n")[1] : prompt.Split("【源文】\n")[1].Split("\n\n【约束要求】")[0];
            return source.Replace("Hello", "你好", StringComparison.Ordinal).Replace("world", "世界", StringComparison.Ordinal);
        });
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format, kind);
        var files = new Dictionary<string, string>
        {
            ["sample.srt"] = "1\r\n00:00:01,000 --> 00:00:03,000\r\n<b>Hello</b>\r\nworld\r\n",
            ["sample.vtt"] = "WEBVTT\n\nNOTE protected\nmetadata\n\ncue\n00:01.000 --> 00:03.000 align:start\n<b>Hello</b>\nworld\n",
            ["sample.html"] = "<html><body><p>Hello <b>world</b></p></body></html>",
            ["sample.md"] = "# Hello\n\n**world**\n",
            ["sample.txt"] = "Hello world\r\n"
        };
        using var form = new MultipartFormDataContent();
        foreach (var file in files) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file.Value)), "files", file.Key);
        var imported = await client.PostAsync("/api/tasks/imports/files", form);
        imported.EnsureSuccessStatusCode();
        var taskId = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();
        var run = await Run(client, taskId);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var segments = (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments")).GetProperty("items");
        Assert.Equal(segments.GetArrayLength(), handler.Requests.Count);
        foreach (var name in files.Keys) Assert.Contains(segments.EnumerateArray(), segment => segment.GetProperty("sourceFileName").GetString() == name);
        var exported = await client.GetAsync($"/api/tasks/{taskId}/result");
        exported.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(new MemoryStream(await exported.Content.ReadAsByteArrayAsync()));
        Assert.Equal(files.Count, archive.Entries.Count);
        foreach (var file in files)
        {
            using var reader = new StreamReader(archive.GetEntry(file.Key)!.Open(), Encoding.UTF8);
            Assert.Equal(file.Value.Replace("Hello", "你好", StringComparison.Ordinal).Replace("world", "世界", StringComparison.Ordinal), await reader.ReadToEndAsync());
        }
    }

    [Theory]
    [InlineData("hy-mt2", "Hello", "〖待翻译文本〗你好", "你好")]
    [InlineData("hy-mt2", "【待翻译文本】 Hello", "【待翻译文本】 你好", "【待翻译文本】 你好")]
    [InlineData("bilibili-index-translate", "【源文】 Hello", "【源文】 你好", "【源文】 你好")]
    public async Task SourceTemplateMarkersArePreserved(string format, string source, string output, string expected)
    {
        var handler = new CaptureHandler("openai", (_, _) => output);
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format);
        var taskId = await SpecializedTestApi.Import(client, source);
        var run = await Run(client, taskId);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        Assert.Equal(expected, segments.GetProperty("items")[0].GetProperty("targetText").GetString());
    }

    [Theory]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task TermsRespectLanguageCaseAndWholeEntryBudget(string format)
    {
        var handler = new CaptureHandler("openai", (_, _) => "你好，世界。");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format);
        await AddTerm(client, "hello", "无视大小写命中");
        await AddTerm(client, "WORLD", "不应大小写命中", caseSensitive: true);
        await AddTerm(client, "Hello", "不应语言命中", language: "fr");
        var longTerm = string.Concat(Enumerable.Repeat("😀", 450));
        await AddTerm(client, longTerm, string.Concat(Enumerable.Repeat("😁", 450)));
        var taskId = await SpecializedTestApi.Import(client, "Hello world " + longTerm);
        var run = await Run(client, taskId);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var prompt = handler.Requests.Single().GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("无视大小写命中", prompt);
        Assert.DoesNotContain("不应大小写命中", prompt);
        Assert.DoesNotContain("不应语言命中", prompt);
        Assert.DoesNotContain("😁", prompt);
        Assert.Contains(longTerm, prompt);
    }
}
