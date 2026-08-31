namespace MyTranslator.Shared.Models;

/// <summary>
/// 历史翻译对比接口（docs/back/TM 接口约定.md §6）的 JSON 模型。
/// 字段语义与形状见 §6.3；后端是匹配分数、差异分数与告警结论的唯一计算方，
/// 前端只渲染数值与逐字差异，不得自行判定告警。
/// </summary>
public static class TmComparisonJson
{
    /// <summary>序列化选项：camelCase 命名 + 大小写不敏感反序列化（§2.1）。</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Options = ImportJson.Options;
}

/// <summary>v1 匹配与告警阈值（§2.4 / §6.3）；由服务端固定并在每次对比响应中回显。</summary>
public sealed record TmThresholds
{
    /// <summary>返回为相似历史译文的最低源文相似度（v1: 0.7000）。</summary>
    public double MinimumSourceMatchScore { get; init; }

    /// <summary>允许成为差异告警参考项的最低源文相似度（v1: 0.8500）。</summary>
    public double WarningSourceMatchScore { get; init; }

    /// <summary>当前译文与参考历史译文的差异分数大于该值时告警（v1: 0.3000）。</summary>
    public double WarningTargetDifference { get; init; }
}

/// <summary>一条相似历史译文匹配项（§6.3 items）。</summary>
public sealed record TmComparisonItem
{
    /// <summary>TM 条目 ID。</summary>
    public Guid EntryId { get; init; }

    /// <summary>历史源文。</summary>
    public string? SourceText { get; init; }

    /// <summary>历史译文。</summary>
    public string? TargetText { get; init; }

    /// <summary>条目只读标记表。</summary>
    public IReadOnlyList<MarkupItem> MarkupTable { get; init; } = [];

    /// <summary>当前源文与历史源文的 0..1 相似度（四位小数）。</summary>
    public double? SourceMatchScore { get; init; }

    /// <summary>当前译文与历史译文的 0..1 相似度；当前译文为 null 时为 null。</summary>
    public double? TargetSimilarity { get; init; }

    /// <summary>1 - TargetSimilarity，四位小数；当前译文为 null 时为 null。</summary>
    public double? TargetDifference { get; init; }

    /// <summary>条目首次写入时间。</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>差异告警结论（§6.3 differenceWarning）；后端唯一判定方。</summary>
public sealed record TmDifferenceWarning
{
    /// <summary>仅当参考项存在且其 targetDifference 超过阈值时为 true。</summary>
    public bool HasWarning { get; init; }

    /// <summary>第一条高置信匹配的条目 ID；无合格参考或当前译文为 null 时为 null。</summary>
    public Guid? ReferenceEntryId { get; init; }

    /// <summary>参考项源文相似度；无参考时为 null。</summary>
    public double? SourceMatchScore { get; init; }

    /// <summary>参考项译文差异分数；无参考时为 null。</summary>
    public double? TargetDifference { get; init; }

    /// <summary>告警时为 `target_difference_exceeded`；无告警时为 null。</summary>
    public string? Reason { get; init; }
}

/// <summary>历史翻译对比响应（§6.3）。</summary>
public sealed record TmComparisonResponse
{
    /// <summary>任务 ID；通用对比（§6.2）为 null。</summary>
    public Guid? TaskId { get; init; }

    /// <summary>分段 ID；通用对比为 null。</summary>
    public Guid? SegmentId { get; init; }

    /// <summary>本次对比绑定的提取修订。</summary>
    public int? ExtractionRevision { get; init; }

    /// <summary>本次对比绑定的分段版本。</summary>
    public int? SegmentVersion { get; init; }

    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }

    /// <summary>v1 阈值回显。</summary>
    public TmThresholds? Thresholds { get; init; }

    /// <summary>按服务端顺序（源文分数降序、createdAt 降序、entryId 升序）的匹配项。</summary>
    public IReadOnlyList<TmComparisonItem> Items { get; init; } = [];

    /// <summary>差异告警结论。服务端兼容性地省略该字段时按无告警（防御兼容）。</summary>
    public TmDifferenceWarning? DifferenceWarning { get; init; }

    /// <summary>后端完成本次快照计算的时间。</summary>
    public DateTimeOffset? ComparedAt { get; init; }
}
