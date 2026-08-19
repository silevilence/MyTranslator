namespace MyTranslator.Shared.Models;

/// <summary>
/// 单条 AI 审核意见（docs/back/AI 审核接口约定.md §8.2 公开形状）。
/// 只读的当前有效结果，不绑定分段 version，不提供编辑或一键应用。
/// </summary>
public sealed record ReviewComment
{
    /// <summary>`high` / `medium` / `low`；服务端已将非法值兜底为 `medium`。</summary>
    public string? Severity { get; init; }

    /// <summary>问题描述；去除首尾空白后 1..2000 个 Unicode 标量值。</summary>
    public string? Issue { get; init; }

    /// <summary>修改建议；缺失、null 或纯空白统一为 null。</summary>
    public string? Suggestion { get; init; }
}

/// <summary>审核运行的选中统计（§4 运行资源字段约束）。</summary>
public sealed record ReviewRunSelection
{
    /// <summary>当前提取修订内全部可译分段数。</summary>
    public int TotalSegments { get; init; }

    /// <summary>创建时 targetText 非空白的分段数；本次运行的固定工作量。</summary>
    public int SelectedSegments { get; init; }

    /// <summary>创建时 targetText = null 的分段数。</summary>
    public int SkippedUntranslatedSegments { get; init; }
}

/// <summary>审核运行进度（§4 / §5）。</summary>
public sealed record ReviewRunProgress
{
    /// <summary>已进入成功或失败终态的本次选中分段数。</summary>
    public int ProcessedSegments { get; init; }

    /// <summary>已成功校验并提交本轮意见的分段数；返回 0 条意见也计为成功。</summary>
    public int SucceededSegments { get; init; }

    /// <summary>已耗尽重试或不可重试失败的分段数。</summary>
    public int FailedSegments { get; init; }

    /// <summary>0–100，保留一位小数；运行期间不得下降。</summary>
    public double Percent { get; init; }
}

/// <summary>终态失败摘要（§11.2）。</summary>
public sealed record ReviewRunFailure
{
    /// <summary>§11.2 定义的稳定异步失败码。</summary>
    public string? Code { get; init; }

    /// <summary>重新创建完整审核运行前是否值得重试；不保证下一次成功。</summary>
    public bool Retryable { get; init; }

    /// <summary>失败分段数。</summary>
    public int FailedSegments { get; init; }
}

/// <summary>
/// 审核运行（§4 创建响应 / §5 查询 / §6 列表共用同一形状）。
/// 状态枚举：`queued` / `processing` / `completed` / `partial_failed` / `failed`。
/// </summary>
public sealed record ReviewRun
{
    public Guid RunId { get; init; }
    public Guid TaskId { get; init; }

    /// <summary>运行创建时的提取修订；运行生命周期内不变。</summary>
    public int ExtractionRevision { get; init; }

    public string? Status { get; init; }

    /// <summary>null 表示由 LLM 自动识别源语言（且不注入术语）。</summary>
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }

    /// <summary>解析后的实际提供商 ID；运行创建成功后服务端恒非空（前端可显示为回显）。</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>解析后的实际模型条目 ID；同上。</summary>
    public Guid? ModelId { get; init; }

    public ReviewRunSelection? Selection { get; init; }
    public ReviewRunProgress? Progress { get; init; }

    /// <summary>活动或成功运行为 null；终态失败时为摘要。</summary>
    public ReviewRunFailure? Failure { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>审核运行列表分页响应（§6）。</summary>
public sealed record ReviewRunPage
{
    public int ExtractionRevision { get; init; }
    public IReadOnlyList<ReviewRun> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

/// <summary>审核分段级失败（§7 字段约束）。</summary>
public sealed record SegmentReviewFailure
{
    public Guid SegmentId { get; init; }

    /// <summary>运行创建时的分段顺序，用于前端定位。</summary>
    public int SegmentOrder { get; init; }

    /// <summary>§11.2 定义的稳定异步失败码。</summary>
    public string? Code { get; init; }

    /// <summary>重新创建完整审核运行前该分段是否值得重试。</summary>
    public bool Retryable { get; init; }

    /// <summary>本次运行对所属批次实际发起的 LLM 请求次数。</summary>
    public int Attempts { get; init; }
}

/// <summary>审核分段级失败分页响应（§7）。</summary>
public sealed record ReviewFailurePage
{
    public Guid RunId { get; init; }
    public IReadOnlyList<SegmentReviewFailure> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}
