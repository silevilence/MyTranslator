using System.Text.Json;

namespace MyTranslator.Shared.Models;

/// <summary>
/// 文件导入拆解与导出接口的 JSON 序列化公共选项。
/// 契约（docs/back/文件导入拆解与导出接口约定.md）§2.1：JSON 字段使用 camelCase。
/// </summary>
public static class ImportJson
{
    /// <summary>Web 默认序列化：camelCase 命名 + 大小写不敏感反序列化。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>来源类型：文件上传或 URL 导入。</summary>
public sealed record TaskSource
{
    /// <summary>`file` 或 `url`。</summary>
    public string? Kind { get; init; }

    /// <summary>来源文件名（URL 来源为后端生成的建议下载名）。</summary>
    public string? FileName { get; init; }

    /// <summary>URL 来源的原始请求地址；文件来源为 null。</summary>
    public string? RequestedUrl { get; init; }

    /// <summary>URL 来源重定向后的最终地址；文件来源为 null。</summary>
    public string? FinalUrl { get; init; }

    /// <summary>媒体类型。</summary>
    public string? MediaType { get; init; }

    /// <summary>字节数。</summary>
    public long? ByteLength { get; init; }
}

/// <summary>txt 分段信息；非 txt 任务为 null。</summary>
public sealed record TaskSegmentation
{
    /// <summary>调用方显式请求的模式；未传为 null。</summary>
    public string? Requested { get; init; }

    /// <summary>智能推荐模式。</summary>
    public string? Recommended { get; init; }

    /// <summary>实际生效模式。</summary>
    public string? Effective { get; init; }

    /// <summary>推荐原因：`blank_line_blocks_present` 或 `few_or_no_blank_lines`。</summary>
    public string? Reason { get; init; }
}

/// <summary>导入计数。</summary>
public sealed record TaskCounts
{
    public int Segments { get; init; }
    public int ProtectedBlocks { get; init; }
    public int Chapters { get; init; }
}

/// <summary>任务能力标识。</summary>
public sealed record TaskCapabilities
{
    /// <summary>是否支持重新提取（html 文件 / URL HTML / epub）。</summary>
    public bool CanReextract { get; init; }

    /// <summary>当前是否满足公开导出条件。</summary>
    public bool CanExport { get; init; }
}

/// <summary>任务导入摘要（§4.3 响应形状）。</summary>
public sealed record TaskSummary
{
    /// <summary>当前译文完成与人工确认进度。</summary>
    public TaskProgress? Progress { get; init; }
    public Guid TaskId { get; init; }
    public string? Status { get; init; }
    public TaskSource? Source { get; init; }
    public string? FileType { get; init; }
    public string? OriginalEncoding { get; init; }
    public int ExtractionRevision { get; init; }
    public TaskSegmentation? Segmentation { get; init; }
    public TaskCounts? Counts { get; init; }
    public TaskCapabilities? Capabilities { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>分段标记表项（§7 契约）。</summary>
public sealed record MarkupItem
{
    public int Id { get; init; }

    /// <summary>`paired` 或 `standalone`。</summary>
    public string? Kind { get; init; }

    public string? OpeningText { get; init; }
    public string? ClosingText { get; init; }
    public string? OriginalText { get; init; }

    /// <summary>人类可读含义提示（如 `&nbsp;` → 不间断空格），可能为 null。</summary>
    public string? Meaning { get; init; }
}

/// <summary>epub 章/文档部分归属（§8 契约）。</summary>
public sealed record Chapter
{
    public Guid Id { get; init; }
    public int Order { get; init; }
    public int? SpineIndex { get; init; }
    public string? ResourcePath { get; init; }
    public string? Title { get; init; }

    /// <summary>`content` 或 `navigation`。</summary>
    public string? Role { get; init; }
}

/// <summary>可译分段（§6 契约）。</summary>
public sealed record Segment
{
    public Guid Id { get; init; }

    /// <summary>当前提取修订内从 1 开始连续递增。</summary>
    public int Order { get; init; }

    /// <summary>可译原文，含与 <see cref="MarkupTable"/> 对应的占位符引用。</summary>
    public string? SourceText { get; init; }

    /// <summary>译文；未翻译为 null（不得用空字符串代替）。</summary>
    public string? TargetText { get; init; }

    /// <summary>`pending` / `translated` / `confirmed`。</summary>
    public string? ConfirmationStatus { get; init; }

    /// <summary>乐观并发版本。</summary>
    public int Version { get; init; }

    public IReadOnlyList<MarkupItem> MarkupTable { get; init; } = [];

    /// <summary>
    /// 当前有效的 AI 审核意见（docs/back/AI 审核接口约定.md §9），按严重度降序；
    /// 无意见时为空数组；调用方须容忍兼容服务省略该字段并按空数组处理。
    /// </summary>
    public IReadOnlyList<ReviewComment> ReviewComments { get; init; } = [];

    public Chapter? Chapter { get; init; }
}

/// <summary>分段分页响应（§2.2 / §6）。</summary>
public sealed record SegmentPage
{
    public int ExtractionRevision { get; init; }
    public int TotalCount { get; init; }
    public IReadOnlyList<Segment> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

/// <summary>重新提取预览的译文损失统计（§11.1）。</summary>
public sealed record TranslationLoss
{
    public int TranslatedSegments { get; init; }
    public int ConfirmedSegments { get; init; }
    public bool RequiresConfirmation { get; init; }
}

/// <summary>重新提取预览摘要（§11.1 响应形状）。</summary>
public sealed record ReextractionPreview
{
    public Guid PreviewId { get; init; }
    public Guid TaskId { get; init; }
    public string? Selector { get; init; }
    public int BaseExtractionRevision { get; init; }
    public TaskCounts? Counts { get; init; }
    public TranslationLoss? TranslationLoss { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>导出下载结果。</summary>
/// <param name="Content">回填还原后的文件字节。</param>
/// <param name="FileName">服务端建议的下载文件名（含 .translated 后缀）。</param>
/// <param name="MediaType">响应 Content-Type。</param>
public sealed record ExportResult(byte[] Content, string FileName, string MediaType);

/// <summary>任务列表项的译文进度（§4.5）。</summary>
public sealed record TaskProgress
{
    /// <summary>当前提取修订内 targetText != null 的分段数。</summary>
    public int CompletedSegments { get; init; }

    /// <summary>当前提取修订内全部可译分段数。</summary>
    public int TotalSegments { get; init; }

    /// <summary>人工确认数，独立于已有译文数。</summary>
    public int ConfirmedSegments { get; init; }

    /// <summary>完成度百分比（0–100，保留一位小数）；无分段时为 100，与任务接口一致。</summary>
    public double Percent => TotalSegments == 0
        ? 100
        : Math.Round(CompletedSegments * 100.0 / TotalSegments, 1);
    /// <summary>确认度百分比。</summary>
    public double ConfirmedPercent => TotalSegments == 0 ? 100 : Math.Round(ConfirmedSegments * 100.0 / TotalSegments, 1);
}

/// <summary>
/// 任务列表项的最新翻译运行摘要（§4.5）。
/// 状态与进度形状复用《AI 翻译接口约定》§2.3 / §4 / §10.2。
/// </summary>
public sealed record LatestTranslationRun
{
    public Guid RunId { get; init; }

    /// <summary>运行状态：`queued` / `processing` / `completed` / `partial_failed` / `failed`。</summary>
    public string? Status { get; init; }

    public TranslationRunProgress? Progress { get; init; }

    /// <summary>活动或成功运行为 null；终态失败时为摘要。</summary>
    public TranslationRunFailure? Failure { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>任务列表项（§4.5 响应形状）。</summary>
public sealed record TaskListItem
{
    public Guid TaskId { get; init; }
    public string? Status { get; init; }
    public TaskSource? Source { get; init; }
    public string? FileType { get; init; }
    public int ExtractionRevision { get; init; }
    public TaskProgress? Progress { get; init; }

    /// <summary>按 createdAt、runId 降序选取的当前提取修订最新运行；无运行时为 null。</summary>
    public LatestTranslationRun? LatestTranslationRun { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>任务列表分页响应（§4.5）。</summary>
public sealed record TaskListPage
{
    public IReadOnlyList<TaskListItem> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}
