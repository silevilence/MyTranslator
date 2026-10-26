using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class TranslationExperienceTests
{
    [Fact]
    public async Task CollectionPreservesEpubArchiveAndHtmlMarkup()
    {
        var entries = new Dictionary<string, string>
        {
            ["mimetype"] = "application/epub+zip",
            ["META-INF/container.xml"] = "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"content.opf\"/></rootfiles></container>",
            ["content.opf"] = "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>",
            ["chapter.xhtml"] = "<html><body><p>Chapter <em>one</em></p><pre>Keep code</pre></body></html>",
            ["style.css"] = "body { color: black; }"
        };
        using var original = new MemoryStream();
        using (var epub = new ZipArchive(original, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(epub.CreateEntry(entry.Key, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
                writer.Write(entry.Value);
            }
        using var factory = new ApiFactory();
        using var client = Client(factory);
        const string html = "<html><body><p>Web <strong>text</strong></p><script>keep();</script></body></html>";
        using var form = Files(("page.html", html));
        form.Add(new ByteArrayContent(original.ToArray()), "files", "book.epub");
        var response = await client.PostAsync("/api/tasks/imports/files", form);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var segments = await db.TranslationSegments.Where(s => s.TaskId == id).ToListAsync();
            Assert.Contains(segments, s => s.SourceFileName == "book.epub" && s.ChapterJson != null);
            foreach (var segment in segments) segment.TargetText = segment.SourceText;
            await db.SaveChangesAsync();
        }
        using var exported = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"/api/tasks/{id}/result")));
        using var page = new StreamReader(exported.GetEntry("page.html")!.Open());
        Assert.Equal(html, await page.ReadToEndAsync());
        using var epubBytes = new MemoryStream();
        await using (var stream = exported.GetEntry("book.epub")!.Open()) await stream.CopyToAsync(epubBytes);
        epubBytes.Position = 0;
        using var output = new ZipArchive(epubBytes);
        Assert.Equal("mimetype", output.Entries[0].FullName);
        foreach (var entry in entries)
        {
            using var reader = new StreamReader(output.GetEntry(entry.Key)!.Open());
            Assert.Equal(entry.Value, await reader.ReadToEndAsync());
        }
    }

    [Theory]
    [InlineData(21, 1, "invalid_file_collection", HttpStatusCode.BadRequest)]
    [InlineData(2, 6 * 1024 * 1024, "content_too_large", HttpStatusCode.RequestEntityTooLarge)]
    public async Task CollectionLimitsCreateNoTask(int count, int size, string code, HttpStatusCode status)
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        using var form = Files(Enumerable.Range(0, count).Select(i => ($"{i}.txt", new string('a', size))).ToArray());
        var response = await client.PostAsync("/api/tasks/imports/files", form);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().TranslationTasks.CountAsync());
    }

    [Fact]
    public async Task MultipleFilesRoundTripThroughOneTaskWithOriginalBoundaries()
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        using var form = Files(("a.txt", "One\r\n\r\nTwo"), ("b.md", "# Title\n\n```\nkeep\n```\n"));
        var response = await client.PostAsync("/api/tasks/imports/files", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = task.GetProperty("taskId").GetGuid();
        Assert.Equal("bundle", task.GetProperty("fileType").GetString());
        Assert.Equal(2, task.GetProperty("files").GetArrayLength());
        Assert.False(task.GetProperty("capabilities").GetProperty("canReextract").GetBoolean());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var segments = await db.TranslationSegments.Where(s => s.TaskId == id).OrderBy(s => s.Order).ToListAsync();
            Assert.Equal(new[] { "a.txt", "a.txt", "b.md" }, segments.Select(s => s.SourceFileName));
            Assert.Equal(new[] { 1, 2, 3 }, segments.Select(s => s.Order));
            foreach (var segment in segments) segment.TargetText = segment.SourceText;
            await db.SaveChangesAsync();
        }
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal(2, detail.GetProperty("files").GetArrayLength());
        var result = await client.GetAsync($"/api/tasks/{id}/result");
        result.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", result.Content.Headers.ContentType!.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await result.Content.ReadAsByteArrayAsync()));
        Assert.Equal(2, archive.Entries.Count);
        using var first = new StreamReader(archive.GetEntry("a.txt")!.Open());
        using var second = new StreamReader(archive.GetEntry("b.md")!.Open());
        Assert.Equal("One\r\n\r\nTwo", await first.ReadToEndAsync());
        Assert.Equal("# Title\n\n```\nkeep\n```\n", await second.ReadToEndAsync());
    }

    [Theory]
    [InlineData("a.txt", "A.txt", "valid", "invalid_file_collection")]
    [InlineData("a.txt", "b.txt", "", "import_parse_failed")]
    [InlineData("a.txt", "../b.txt", "valid", "invalid_file_collection")]
    public async Task InvalidCollectionCreatesNoTask(string first, string second, string content, string code)
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        using var form = Files((first, "valid"), (second, content));
        var response = await client.PostAsync("/api/tasks/imports/files", form);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().TranslationTasks.CountAsync());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(12, false)]
    public async Task ReasoningReachesProviderAndWaitingActivityIsVisibleBeforeResponse(int count, bool retry)
    {
        using var handler = new BlockingHandler(retry);
        using var factory = new ApiFactory("Development", null, translationHandler: handler);
        using var client = Client(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var model = await db.Models.SingleAsync();
            model.SupportsThinking = true;
            (await db.Providers.SingleAsync()).BatchSize = 1;
            await db.SaveChangesAsync();
        }
        using var form = Files(("a.txt", string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"Hello {i}"))));
        var imported = await (await client.PostAsync("/api/tasks/imports/files", form)).Content.ReadFromJsonAsync<JsonElement>();
        var taskId = imported.GetProperty("taskId").GetGuid();
        var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, targetLanguage = "zh-CN", reasoningEffort = "high" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("runId").GetGuid();
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("high", handler.Effort);
            var running = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs/{runId}");
            Assert.Equal("high", running.GetProperty("reasoningEffort").GetString());
            Assert.Equal(0, running.GetProperty("progress").GetProperty("processedSegments").GetInt32());
            var activity = Assert.Single(running.GetProperty("events").EnumerateArray());
            Assert.Equal("requesting", activity.GetProperty("stage").GetString());
            Assert.Equal(1, activity.GetProperty("segmentCount").GetInt32());
        }
        finally { handler.Release.TrySetResult(); }
        for (var i = 0; i < 100; i++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs/{runId}");
            if (run.GetProperty("status").GetString() == "completed")
            {
                var events = run.GetProperty("events").EnumerateArray().ToArray();
                var expected = retry
                    ? new[] { "requesting", "validating", "retrying", "requesting", "validating", "saving" }
                    : Enumerable.Range(0, count).SelectMany(_ => new[] { "requesting", "validating", "saving" }).TakeLast(30).ToArray();
                Assert.Equal(expected, events.Select(e => e.GetProperty("stage").GetString()));
                Assert.Equal(count, run.GetProperty("progress").GetProperty("succeededSegments").GetInt32());
                if (retry) Assert.Equal("llm_response_invalid", events[2].GetProperty("code").GetString());
                if (count > 10) Assert.Equal(3, events[0].GetProperty("firstSegmentOrder").GetInt32());
                return;
            }
            await Task.Delay(30);
        }
        Assert.Fail("Run did not complete.");
    }

    [Theory]
    [InlineData("maximum", "invalid_reasoning_effort")]
    [InlineData("high", "reasoning_not_supported")]
    public async Task InvalidReasoningDoesNotCreateRun(string effort, string code)
    {
        using var handler = new BlockingHandler();
        using var factory = new ApiFactory("Development", null, translationHandler: handler);
        using var client = Client(factory);
        using var form = Files(("a.txt", "Hello"));
        var task = await (await client.PostAsync("/api/tasks/imports/files", form)).Content.ReadFromJsonAsync<JsonElement>();
        var id = task.GetProperty("taskId").GetGuid();
        var response = await client.PostAsJsonAsync($"/api/tasks/{id}/translation-runs",
            new { extractionRevision = 1, targetLanguage = "zh-CN", reasoningEffort = effort });
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}/translation-runs");
        Assert.Empty(runs.GetProperty("items").EnumerateArray());
    }

    private static HttpClient Client(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-dev-00000000000000000000000000000000");
        return client;
    }

    private static MultipartFormDataContent Files(params (string Name, string Text)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var file in files) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file.Text)), "files", file.Name);
        return form;
    }

    private sealed class BlockingHandler(bool invalidFirst = false) : HttpMessageHandler
    {
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Effort { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Effort = body.RootElement.GetProperty("reasoning_effort").GetString();
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var translated = JsonSerializer.Serialize(new
            {
                translations = input.RootElement.GetProperty("segments").EnumerateArray().Select(s =>
                    new { segmentId = s.GetProperty("segmentId").GetGuid(), targetText = "你好" }).ToArray()
            });
            if (invalidFirst && Interlocked.Increment(ref _calls) == 1) translated = "{\"translations\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "test", choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = translated } } } })
            };
        }
    }
}
