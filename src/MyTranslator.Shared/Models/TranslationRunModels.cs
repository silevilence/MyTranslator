namespace MyTranslator.Shared.Models;

/// <summary>
/// AI 翻译运行的选中统计（docs/back/AI 翻译接口约定.md §4 运行资源字段约束）。
/// </summary>
public sealed record TranslationRunSelection
{
    /// <summary>当前提取修订内全部可译分段数。</summary>
    public int TotalSegments { get; init; }

    /// <summary>创建时 targetText = null 的分段数；本次运行的固定工作量。</summary>
    public int SelectedSegments { get; init; }

    /// <summary>创建时已有非空白译文的分段数（含 translated 与 confirmed）。</summary>
    public int SkippedExistingSegments { get; init; }
}

/// <summary>翻译运行进度（§4 / §5）。</summary>
public sealed record TranslationRunProgress
{
    /// <summary>已进入成功或失败终态的本次选中分段数。</summary>
    public int ProcessedSegments { get; init; }

    /// <summary>已由本次运行成功保存译文的分段数。</summary>
    public int SucceededSegments { get; init; }

    /// <summary>已耗尽重试或不可重试的分段数。</summary>
    public int FailedSegments { get; init; }

    /// <summary>0–100，保留一位小数；运行期间不得下降。</summary>
    public double Percent { get; init; }
}

/// <summary>终态失败摘要（§7.2 / §10.2）。</summary>
public sealed record TranslationRunFailure
{
    /// <summary>§10.2 定义的稳定机器可读失败码。</summary>
    public string? Code { get; init; }

    /// <summary>按相同语言参数再次创建运行是否合理。</summary>
    public bool Retryable { get; init; }

    /// <summary>失败分段数。</summary>
    public int FailedSegments { get; init; }
}

/// <summary>
/// AI 翻译运行（§4 创建响应 / §5 查询 / §6 列表共用同一形状）。
/// 状态枚举：`queued` / `processing` / `completed` / `partial_failed` / `failed`。
/// </summary>
public sealed record TranslationRun
{
    public Guid RunId { get; init; }
    public Guid TaskId { get; init; }

    /// <summary>运行创建时的提取修订；运行生命周期内不变。</summary>
    public int ExtractionRevision { get; init; }

    public string? Status { get; init; }

    /// <summary>null 表示由 LLM 自动识别源语言。</summary>
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }

    /// <summary>解析后的实际提供商 ID；运行创建成功后服务端恒非空（前端可显示为回显）。</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>解析后的实际模型条目 ID；同上。</summary>
    public Guid? ModelId { get; init; }

    public TranslationRunSelection? Selection { get; init; }
    public TranslationRunProgress? Progress { get; init; }

    /// <summary>活动或成功运行为 null；终态失败时为摘要。</summary>
    public TranslationRunFailure? Failure { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>翻译运行列表分页响应（§6）。</summary>
public sealed record TranslationRunPage
{
    public int ExtractionRevision { get; init; }
    public IReadOnlyList<TranslationRun> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

/// <summary>分段级失败（§7 字段约束）。</summary>
public sealed record SegmentTranslationFailure
{
    public Guid SegmentId { get; init; }

    /// <summary>运行创建时的分段顺序，用于前端定位。</summary>
    public int SegmentOrder { get; init; }

    /// <summary>§10.2 定义的稳定机器可读失败码。</summary>
    public string? Code { get; init; }

    /// <summary>再次创建运行时该分段是否值得重试。</summary>
    public bool Retryable { get; init; }

    /// <summary>本次运行对所属批次实际发起的 LLM 请求次数。</summary>
    public int Attempts { get; init; }
}

/// <summary>分段级失败分页响应（§7）。</summary>
public sealed record TranslationFailurePage
{
    public Guid RunId { get; init; }
    public IReadOnlyList<SegmentTranslationFailure> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}
