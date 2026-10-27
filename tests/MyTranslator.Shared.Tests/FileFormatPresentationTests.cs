using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Tests;

public class FileFormatPresentationTests
{
    [Theory]
    [InlineData("captions.SRT", "text/plain", "srt")]
    [InlineData("captions.VTT", "application/octet-stream", "vtt")]
    [InlineData("captions", "text/vtt; charset=utf-8", "vtt")]
    [InlineData("captions", "application/x-subrip", "srt")]
    [InlineData("notes.txt", "text/vtt", "txt")]
    [InlineData("unknown", "application/octet-stream", null)]
    public void 导入提示按扩展名优先并支持媒体类型回退(string name, string contentType, string? expected)
        => Assert.Equal(expected, FileFormatPresentation.Infer(name, contentType));

    [Fact]
    public void 编辑提示采用显式导入后的持久化格式()
    {
        var segment = new Segment { SourceFileName = "captions.txt" };
        Assert.Equal("srt", FileFormatPresentation.ForSegment("srt", null, segment));
        Assert.Equal("txt", FileFormatPresentation.ForSegment("txt", null, segment with { SourceFileName = "notes.vtt" }));
    }

    [Fact]
    public void 混合任务按当前成员切换提示且不猜测未知成员格式()
    {
        TaskFileSummary[] files = [new("captions.srt", "srt", 200), new("notes.txt", "txt", 100)];
        Assert.Equal("srt", FileFormatPresentation.ForSegment("bundle", files, new Segment { SourceFileName = "captions.srt" }));
        Assert.Equal("txt", FileFormatPresentation.ForSegment("bundle", files, new Segment { SourceFileName = "notes.txt" }));
        Assert.Null(FileFormatPresentation.ForSegment("bundle", files, new Segment { SourceFileName = "unknown.vtt" }));
        Assert.Null(FileFormatPresentation.ForSegment("bundle", null, new Segment { SourceFileName = "captions.srt" }));
    }
}
