namespace MyTranslator.Shared.Models;

/// <summary>界面中的文件格式提示；不解析正文、不替代后端格式校验。</summary>
public static class FileFormatPresentation
{
    /// <summary>导入前按扩展名、媒体类型展示预期格式；显式选择由调用方优先采用。</summary>
    public static string? Infer(string fileName, string? contentType) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" => "txt",
        ".md" or ".markdown" => "markdown",
        ".html" or ".htm" => "html",
        ".epub" => "epub",
        ".srt" => "srt",
        ".vtt" => "vtt",
        _ => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "text/plain" => "txt",
            "text/markdown" or "text/x-markdown" => "markdown",
            "text/html" or "application/xhtml+xml" => "html",
            "application/epub+zip" => "epub",
            "application/x-subrip" => "srt",
            "text/vtt" => "vtt",
            _ => null
        }
    };

    /// <summary>编辑时只信任任务/成员的持久化格式；不按分段文件名后缀猜测。</summary>
    public static string? ForSegment(string? taskFileType, IReadOnlyList<TaskFileSummary>? files, Segment? segment)
        => taskFileType == "bundle"
            ? files?.FirstOrDefault(file => string.Equals(file.FileName, segment?.SourceFileName, StringComparison.Ordinal))?.FileType
            : taskFileType;

    /// <summary>是否为已支持的字幕格式。</summary>
    public static bool IsSubtitle(string? fileType) => fileType is "srt" or "vtt";
}
