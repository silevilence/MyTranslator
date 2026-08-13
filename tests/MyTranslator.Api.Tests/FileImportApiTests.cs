using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class FileImportApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task TxtUploadCreatesPersistentTaskAndParagraphSegments()
    {
        using var client = CreateClient();
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("First line\nsoft line\n\nSecond paragraph"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        request.Add(file, "file", "sample.txt");
        request.Add(new StringContent("paragraph"), "segmentationMode");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("created", created.GetProperty("status").GetString());
        Assert.Equal("txt", created.GetProperty("fileType").GetString());
        Assert.Equal("paragraph", created.GetProperty("segmentation").GetProperty("effective").GetString());
        Assert.Equal(2, created.GetProperty("counts").GetProperty("segments").GetInt32());

        var taskId = created.GetProperty("taskId").GetGuid();
        Assert.Equal($"/api/tasks/{taskId}", createResponse.Headers.Location?.OriginalString);

        var taskResponse = await client.GetAsync($"/api/tasks/{taskId}");
        var segmentResponse = await client.GetAsync($"/api/tasks/{taskId}/segments");
        var segments = await segmentResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, taskResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
        Assert.Equal(1, segments.GetProperty("extractionRevision").GetInt32());
        Assert.Equal(2, segments.GetProperty("totalCount").GetInt32());
        Assert.Collection(
            segments.GetProperty("items").EnumerateArray(),
            first => Assert.Equal("First line\nsoft line", first.GetProperty("sourceText").GetString()),
            second => Assert.Equal("Second paragraph", second.GetProperty("sourceText").GetString()));
    }

    [Fact]
    public async Task MarkdownUploadExtractsMarkupAndProtectsCodeBlocks()
    {
        const string markdown = "# Welcome\n\nText with **bold** and [link](https://example.com).\n\n```csharp\nvar x = 1;\n```";
        using var client = CreateClient();
        using var request = CreateFileRequest(markdown, "guide.md", "text/markdown", "markdown");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("markdown", created.GetProperty("fileType").GetString());
        Assert.Equal(2, created.GetProperty("counts").GetProperty("segments").GetInt32());
        Assert.True(created.GetProperty("counts").GetProperty("protectedBlocks").GetInt32() > 0);

        var taskId = created.GetProperty("taskId").GetGuid();
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var items = segments.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal("<x1/>Welcome", items[0].GetProperty("sourceText").GetString());
        Assert.Equal("Text with <x1>bold</x1> and <x2>link</x2>.", items[1].GetProperty("sourceText").GetString());
        Assert.Equal("# ", items[0].GetProperty("markupTable")[0].GetProperty("originalText").GetString());
        Assert.Equal("**", items[1].GetProperty("markupTable")[0].GetProperty("openingText").GetString());
        Assert.Equal("](https://example.com)", items[1].GetProperty("markupTable")[1].GetProperty("closingText").GetString());
    }

    [Fact]
    public async Task MarkdownUsesCollisionFreePlaceholdersForAllSupportedInlineMarkup()
    {
        const string markdown = "Literal <x1/> with *emphasis*, ![logo](logo.png), and **bold**.\n\n| Name | Value |\n| --- | ---: |\n| One | Two |";
        using var client = CreateClient();
        using var request = CreateFileRequest(markdown, "markup.md", "text/markdown", "markdown");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var items = page.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Contains("<x2>emphasis</x2>", items[0].GetProperty("sourceText").GetString());
        Assert.Contains("<x3/>", items[0].GetProperty("sourceText").GetString());
        Assert.Contains("<x4>bold</x4>", items[0].GetProperty("sourceText").GetString());
        Assert.DoesNotContain(
            items,
            item => item.GetProperty("sourceText").GetString()!.Contains("| --- | ---: |", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiteralPlaceholderLikeTextRemainsTranslatableWithoutMarkupEntry()
    {
        using var client = CreateClient();
        using var request = CreateFileRequest("<x1/>", "literal.txt", "text/plain", "txt");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal("<x1/>", segment.GetProperty("sourceText").GetString());
        Assert.Empty(segment.GetProperty("markupTable").EnumerateArray());
    }

    [Fact]
    public async Task HtmlUploadExtractsTagsAndEntitiesAndExcludesProtectedElements()
    {
        const string html = "<!doctype html><html><body><p>Hello <strong>world</strong>&nbsp;!</p><pre>do not translate</pre><script>secret</script></body></html>";
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "page.html", "text/html", "html");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(created.GetProperty("capabilities").GetProperty("canReextract").GetBoolean());
        Assert.Equal(1, created.GetProperty("counts").GetProperty("segments").GetInt32());

        var taskId = created.GetProperty("taskId").GetGuid();
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());

        Assert.Equal("<x1>Hello <x2>world</x2><x3/>!</x1>", segment.GetProperty("sourceText").GetString());
        var markup = segment.GetProperty("markupTable");
        Assert.Equal("<p>", markup[0].GetProperty("openingText").GetString());
        Assert.Equal("</strong>", markup[1].GetProperty("closingText").GetString());
        Assert.Equal("&nbsp;", markup[2].GetProperty("originalText").GetString());
    }

    [Fact]
    public async Task HtmlAstExtractionHandlesQuotedDelimitersAndProtectsOnlyCodeElement()
    {
        const string html = "<html><body><p title='1>0'>Hello <code>x &lt; y</code> world</p></body></html>";
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "ast.html", "text/html", "html");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Contains("Hello ", segment.GetProperty("sourceText").GetString());
        Assert.Contains(" world", segment.GetProperty("sourceText").GetString());
        Assert.DoesNotContain("x &lt; y", segment.GetProperty("sourceText").GetString());
    }

    [Fact]
    public async Task MalformedHtmlProducesBalancedPlaceholders()
    {
        const string html = "<html><body><p>Hello <b>bold</p></body></html>";
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "malformed.html", "text/html", "html");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal("<x1>Hello <x2/>bold</x1>", segment.GetProperty("sourceText").GetString());
        Assert.Equal("standalone", segment.GetProperty("markupTable")[1].GetProperty("kind").GetString());
        Assert.Equal("<b>", segment.GetProperty("markupTable")[1].GetProperty("originalText").GetString());
    }

    [Theory]
    [InlineData("<html><body>Hello<p>World</p></body></html>", 2, "Hello")]
    [InlineData("Plain body text", 1, "Plain body text")]
    public async Task HtmlExtractsTranslatableTextOutsideBlockElements(
        string html,
        int expectedCount,
        string expectedText)
    {
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "body.html", "text/html", "html");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var sourceTexts = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sourceText").GetString())
            .ToArray();

        Assert.Equal(expectedCount, sourceTexts.Length);
        Assert.Contains(expectedText, sourceTexts);
    }

    [Fact]
    public async Task HtmlSelectorMustMatchInsideBody()
    {
        const string html = "<html><head><title>Head</title></head><body><p>Body</p></body></html>";
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "selector.html", "text/html", "html");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews",
            new { selector = "head" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("selector_no_match", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EpubUploadUsesSpineOrderAndIncludesNavigationDocument()
    {
        using var client = CreateClient();
        using var request = CreateBinaryFileRequest(
            CreateEpub(),
            "book.epub",
            "application/epub+zip",
            "epub");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("originalEncoding").ValueKind);
        Assert.Equal(2, created.GetProperty("counts").GetProperty("chapters").GetInt32());

        var taskId = created.GetProperty("taskId").GetGuid();
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var items = segments.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal("content", items[0].GetProperty("chapter").GetProperty("role").GetString());
        Assert.Equal(0, items[0].GetProperty("chapter").GetProperty("spineIndex").GetInt32());
        Assert.Equal("navigation", items[1].GetProperty("chapter").GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("chapter").GetProperty("spineIndex").ValueKind);
    }

    [Fact]
    public async Task StaticUrlCreatesHtmlTaskFromBackendFetch()
    {
        using var handler = new StaticHtmlHandler("<html><body><p>Fetched page</p></body></html>");
        using var urlFactory = new ApiFactory("Development", null, handler);
        using var client = urlFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);

        var createResponse = await client.PostAsJsonAsync(
            "/api/tasks/imports/url",
            new { url = "https://93.184.216.34/page.html", fileType = "html" });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("url", created.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("https://93.184.216.34/page.html", created.GetProperty("source").GetProperty("finalUrl").GetString());

        var taskId = created.GetProperty("taskId").GetGuid();
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        Assert.Equal(
            "<x1>Fetched page</x1>",
            Assert.Single(segments.GetProperty("items").EnumerateArray()).GetProperty("sourceText").GetString());
    }

    [Fact]
    public async Task UrlImportRejectsIpv6UniqueLocalAddressBeforeFetch()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/tasks/imports/url",
            new { url = "http://[fc00::1]/page.html", fileType = "html" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsafe_source_url", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UrlImportRejectsRedirectToUnsupportedSchemeWithStableProblem()
    {
        using var handler = new RedirectHandler(new Uri("ftp://example.com/file.html"));
        using var urlFactory = new ApiFactory("Development", null, handler);
        using var client = urlFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);

        var response = await client.PostAsJsonAsync(
            "/api/tasks/imports/url",
            new { url = "https://93.184.216.34/page.html", fileType = "html" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_source_url", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SegmentListUsesOpaqueCursorPagination()
    {
        using var client = CreateClient();
        using var request = CreateFileRequest("One\n\nTwo", "paging.txt", "text/plain", "txt");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var firstPage = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments?limit=1");
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/segments?limit=1&cursor={Uri.EscapeDataString(cursor!)}");

        Assert.Equal("One", Assert.Single(firstPage.GetProperty("items").EnumerateArray()).GetProperty("sourceText").GetString());
        Assert.NotNull(cursor);
        Assert.Equal("Two", Assert.Single(secondPage.GetProperty("items").EnumerateArray()).GetProperty("sourceText").GetString());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task SegmentCursorCannotBeReusedForAnotherTask()
    {
        using var client = CreateClient();
        using var firstRequest = CreateFileRequest("One\n\nTwo", "first.txt", "text/plain", "txt");
        var firstCreate = await client.PostAsync("/api/tasks/imports/file", firstRequest);
        var firstTask = await firstCreate.Content.ReadFromJsonAsync<JsonElement>();
        var firstPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{firstTask.GetProperty("taskId").GetGuid()}/segments?limit=1");
        var cursor = firstPage.GetProperty("nextCursor").GetString();

        using var secondRequest = CreateFileRequest("Three\n\nFour", "second.txt", "text/plain", "txt");
        var secondCreate = await client.PostAsync("/api/tasks/imports/file", secondRequest);
        var secondTask = await secondCreate.Content.ReadFromJsonAsync<JsonElement>();
        var response = await client.GetAsync(
            $"/api/tasks/{secondTask.GetProperty("taskId").GetGuid()}/segments?limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_cursor", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SourceUnitsInterleaveSegmentsAndProtectedBlocks()
    {
        using var client = CreateClient();
        using var request = CreateFileRequest("One\n\nTwo", "layout.txt", "text/plain", "txt");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var response = await client.GetAsync($"/api/tasks/{taskId}/source-units");
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = page.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["segment", "protectedBlock", "segment"], items.Select(item => item.GetProperty("kind").GetString()));
        Assert.Equal("\n\n", items[1].GetProperty("protectedBlock").GetProperty("previewText").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("segment").ValueKind);
    }

    [Fact]
    public async Task SourceUnitsReportSpecificProtectedBlockTypes()
    {
        const string markdown = "Text\n\n```csharp\nvar x = 1;\n```";
        using var client = CreateClient();
        using var request = CreateFileRequest(markdown, "types.md", "text/markdown", "markdown");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var sourceUnits = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/source-units");
        var protectedTypes = sourceUnits.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "protectedBlock")
            .Select(item => item.GetProperty("protectedBlock").GetProperty("type").GetString())
            .ToArray();

        Assert.Contains("textWhitespace", protectedTypes);
        Assert.Contains("markdownCodeBlock", protectedTypes);
    }

    [Fact]
    public async Task InvalidPageLimitUsesPaginationProblemCode()
    {
        using var client = CreateClient();
        using var request = CreateFileRequest("Text", "limit.txt", "text/plain", "txt");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.GetAsync($"/api/tasks/{created.GetProperty("taskId").GetGuid()}/segments?limit=201");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_pagination", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HtmlReextractionPreviewsThenReplacesSegments()
    {
        const string html = "<html><body><main id='main'><p>Main text</p></main><aside><p>Aside text</p></aside></body></html>";
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "reextract.html", "text/html", "html");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();
        var originalSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var originalIds = originalSegments.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews",
            new { selector = "#main" });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, previewResponse.StatusCode);
        Assert.Equal(1, preview.GetProperty("counts").GetProperty("segments").GetInt32());
        Assert.Equal(2, originalSegments.GetProperty("totalCount").GetInt32());

        var previewId = preview.GetProperty("previewId").GetGuid();
        var previewSegments = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/extraction-previews/{previewId}/segments");
        Assert.Contains(
            "Main text",
            Assert.Single(previewSegments.GetProperty("items").EnumerateArray()).GetProperty("sourceText").GetString());

        var applyResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews/{previewId}/apply",
            new { confirmTranslationLoss = false });
        var applied = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var currentSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var current = Assert.Single(currentSegments.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
        Assert.Equal(2, applied.GetProperty("extractionRevision").GetInt32());
        Assert.DoesNotContain(current.GetProperty("id").GetGuid(), originalIds);
        Assert.Contains("Main text", current.GetProperty("sourceText").GetString());
    }

    [Fact]
    public async Task WhitespaceOnlyTargetDoesNotRequireTranslationLossConfirmation()
    {
        const string html = "<html><body><main><p>Main text</p></main><aside><p>Aside text</p></aside></body></html>";
        using var client = CreateClient();
        using var request = CreateFileRequest(html, "whitespace.html", "text/html", "html");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var segment = await database.TranslationSegments.FirstAsync(item => item.TaskId == taskId);
            segment.TargetText = "   ";
            await database.SaveChangesAsync();
        }

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews",
            new { selector = "main" });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var applyResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews/{preview.GetProperty("previewId").GetGuid()}/apply",
            new { confirmTranslationLoss = false });

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
    }

    [Fact]
    public async Task EpubReextractionAppliesSelectorAcrossDocuments()
    {
        using var client = CreateClient();
        using var request = CreateBinaryFileRequest(
            CreateEpub(),
            "filtered.epub",
            "application/epub+zip",
            "epub");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews",
            new { selector = "nav" });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, previewResponse.StatusCode);
        Assert.Equal(1, preview.GetProperty("counts").GetProperty("segments").GetInt32());

        var previewId = preview.GetProperty("previewId").GetGuid();
        var applyResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews/{previewId}/apply",
            new { confirmTranslationLoss = false });
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
        Assert.Equal("navigation", segment.GetProperty("chapter").GetProperty("role").GetString());
        Assert.Contains("Contents", segment.GetProperty("sourceText").GetString());
    }

    [Fact]
    public async Task ExportRejectsTaskWithUntranslatedSegments()
    {
        using var client = CreateClient();
        using var request = CreateFileRequest("Translate me", "pending.txt", "text/plain", "txt");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var exportResponse = await client.GetAsync($"/api/tasks/{taskId}/export");
        var problem = await exportResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, exportResponse.StatusCode);
        Assert.Equal("task_not_exportable", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("missingTranslations").GetInt32());
    }

    [Fact]
    public async Task Utf16TaskWithoutSegmentsExportsOriginalBytesExactly()
    {
        var content = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(" \r\n"))
            .ToArray();
        using var client = CreateClient();
        using var request = CreateBinaryFileRequest(content, "blank.txt", "text/plain", "txt");

        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("utf-16", created.GetProperty("originalEncoding").GetString());
        Assert.Equal(0, created.GetProperty("counts").GetProperty("segments").GetInt32());

        var taskId = created.GetProperty("taskId").GetGuid();
        var exportResponse = await client.GetAsync($"/api/tasks/{taskId}/export");

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal(content, await exportResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("blank.translated.txt", exportResponse.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task EpubExportRebuildsValidArchiveAndPreservesUntouchedResources()
    {
        var content = CreateEpub(translatable: false);
        using var client = CreateClient();
        using var request = CreateBinaryFileRequest(
            content,
            "protected.epub",
            "application/epub+zip",
            "epub");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var exportResponse = await client.GetAsync($"/api/tasks/{taskId}/export");
        var exportedBytes = await exportResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        using var originalArchive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        using var exportedArchive = new ZipArchive(new MemoryStream(exportedBytes), ZipArchiveMode.Read);
        Assert.Equal("mimetype", exportedArchive.Entries[0].FullName);
        Assert.Equal(exportedArchive.Entries[0].Length, exportedArchive.Entries[0].CompressedLength);
        Assert.Equal(
            ReadEntry(originalArchive, "OEBPS/content.opf"),
            ReadEntry(exportedArchive, "OEBPS/content.opf"));
    }

    [Fact]
    public async Task FileTypeCanBeInferredFromContentType()
    {
        using var client = CreateClient();
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("<html><body><p>Inferred HTML</p></body></html>"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        request.Add(file, "file", "upload.bin");

        var response = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("html", created.GetProperty("fileType").GetString());
    }

    [Fact]
    public async Task MarkdownListItemsAndQuotesAreExtractedLineByLine()
    {
        const string markdown = "- First item\n- Second item\n\n> Quoted text";
        using var client = CreateClient();
        using var request = CreateFileRequest(markdown, "blocks.md", "text/markdown", "markdown");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var sourceTexts = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sourceText").GetString()!)
            .ToArray();

        Assert.Equal(["<x1/>First item", "<x1/>Second item", "<x1/>Quoted text"], sourceTexts);
    }

    [Fact]
    public async Task MissingFileTaskReturnsStableProblemCode()
    {
        using var client = CreateClient();

        var response = await client.GetAsync($"/api/tasks/{Guid.NewGuid()}");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("task_not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UrlImportUsesResponseCharsetForEncodingDetection()
    {
        using var handler = new EncodedHtmlHandler("<html><body><p>Café</p></body></html>", Encoding.Latin1);
        using var urlFactory = new ApiFactory("Development", null, handler);
        using var client = urlFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);

        var response = await client.PostAsJsonAsync(
            "/api/tasks/imports/url",
            new { url = "https://93.184.216.34/latin.html", fileType = "html" });
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("iso-8859-1", created.GetProperty("originalEncoding").GetString());
        var taskId = created.GetProperty("taskId").GetGuid();
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        Assert.Equal(
            "<x1>Café</x1>",
            Assert.Single(segments.GetProperty("items").EnumerateArray()).GetProperty("sourceText").GetString());
    }

    [Fact]
    public async Task UrlExportPreservesDeclaredEncodingForProtectedHtml()
    {
        const string html = "<html><body><pre>Café</pre></body></html>";
        using var handler = new EncodedHtmlHandler(html, Encoding.Latin1);
        using var urlFactory = new ApiFactory("Development", null, handler);
        using var client = urlFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);

        var createResponse = await client.PostAsJsonAsync(
            "/api/tasks/imports/url",
            new { url = "https://93.184.216.34/protected.html", fileType = "html" });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var exportResponse = await client.GetAsync($"/api/tasks/{taskId}/export");

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal(Encoding.Latin1.GetBytes(html), await exportResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ProtectedBlockByteLengthUsesOriginalEncoding()
    {
        var content = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(" \r\n"))
            .ToArray();
        using var client = CreateClient();
        using var request = CreateBinaryFileRequest(content, "layout.txt", "text/plain", "txt");
        var createResponse = await client.PostAsync("/api/tasks/imports/file", request);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("taskId").GetGuid();

        var sourceUnits = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/source-units");
        var protectedBlock = Assert.Single(sourceUnits.GetProperty("items").EnumerateArray())
            .GetProperty("protectedBlock");

        Assert.Equal(Encoding.Unicode.GetByteCount(" \r\n"), protectedBlock.GetProperty("byteLength").GetInt64());
        Assert.Equal(
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(" \r\n")))}",
            protectedBlock.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task OversizedUploadReturnsStableContentTooLargeProblem()
    {
        using var client = CreateClient();
        using var request = CreateBinaryFileRequest(
            new byte[10 * 1024 * 1024 + 1],
            "large.txt",
            "text/plain",
            "txt");

        var response = await client.PostAsync("/api/tasks/imports/file", request);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("content_too_large", problem.GetProperty("code").GetString());
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);
        return client;
    }

    private static MultipartFormDataContent CreateFileRequest(
        string content,
        string fileName,
        string mediaType,
        string fileType)
    {
        var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        request.Add(file, "file", fileName);
        request.Add(new StringContent(fileType), "fileType");
        return request;
    }

    private static MultipartFormDataContent CreateBinaryFileRequest(
        byte[] content,
        string fileName,
        string mediaType,
        string fileType)
    {
        var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        request.Add(file, "file", fileName);
        request.Add(new StringContent(fileType), "fileType");
        return request;
    }

    private static byte[] CreateEpub(bool translatable = true)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteEntry(
                archive,
                "META-INF/container.xml",
                """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            WriteEntry(
                archive,
                "OEBPS/content.opf",
                """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <manifest>
                    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                  </manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            WriteEntry(
                archive,
                "OEBPS/chapter.xhtml",
                translatable
                    ? "<html><head><title>One</title></head><body><p>Chapter text</p></body></html>"
                    : "<html><head><title>One</title></head><body><pre>Chapter bytes</pre></body></html>");
            WriteEntry(
                archive,
                "OEBPS/nav.xhtml",
                translatable
                    ? "<html><body><nav><p>Contents</p></nav></body></html>"
                    : "<html><body><nav><pre>Navigation bytes</pre></nav></body></html>");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static byte[] ReadEntry(ZipArchive archive, string path)
    {
        using var source = archive.GetEntry(path)!.Open();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private sealed class StaticHtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectHandler(Uri location) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
                Headers = { Location = location }
            });
    }

    private sealed class EncodedHtmlHandler(string html, Encoding encoding) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(encoding.GetBytes(html));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/html")
            {
                CharSet = encoding.WebName
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }
}
