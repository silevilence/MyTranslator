namespace MyTranslator.Shared.Models;

/// <summary>
/// 术语资源（docs/back/术语接口约定.md §3）。
/// 规定一个源语言术语在目标语言中的期望术语；唯一性按语言对内规范化源术语判定。
/// </summary>
public record Term
{
    public Guid Id { get; init; }

    /// <summary>源术语；NFC、去首尾空白后 1..500 个 Unicode 标量值。</summary>
    public string SourceTerm { get; init; } = string.Empty;

    /// <summary>规定使用的期望术语；约束同 <see cref="SourceTerm"/>。</summary>
    public string TargetTerm { get; init; } = string.Empty;

    /// <summary>源语言 BCP 47 标签（服务端规范化大小写后回显）。</summary>
    public string SourceLanguage { get; init; } = string.Empty;

    /// <summary>目标语言 BCP 47 标签。</summary>
    public string TargetLanguage { get; init; } = string.Empty;

    /// <summary>人工说明；无说明时为 null。</summary>
    public string? Notes { get; init; }

    /// <summary>对齐检查是否区分大小写；默认 false。</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>乐观并发版本；创建时为 1，每次成功更新递增。</summary>
    public int Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最近一次成功写入时间；列表与模糊查询按它排序。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// 术语列表项（§5）：在完整术语形状上增加模糊查询专用的 <see cref="MatchScore"/> / <see cref="MatchedField"/>。
/// 无查询时二者为 null；matchScore 只用于排序展示，不得用于对齐判定。
/// </summary>
public sealed record TermListItem : Term
{
    /// <summary>本次查询内的匹配分 0..1（四位小数）；无 query 时为 null。</summary>
    public double? MatchScore { get; init; }

    /// <summary>命中字段：`source` / `target`；无 query 时为 null。</summary>
    public string? MatchedField { get; init; }
}

/// <summary>术语列表页（§5 游标分页）；nextCursor 为 null 表示末页。</summary>
public sealed record TermListPage
{
    public IReadOnlyList<TermListItem> Items { get; init; } = [];

    public string? NextCursor { get; init; }
}

/// <summary>
/// 术语创建/更新请求体（§4 / §7）。更新为完整替换，必须携带读取时的
/// <see cref="Version"/>；创建时版本由服务端签发，该字段被忽略。
/// </summary>
public sealed record TermUpsert
{
    public string SourceTerm { get; init; } = string.Empty;

    public string TargetTerm { get; init; } = string.Empty;

    public string SourceLanguage { get; init; } = string.Empty;

    public string TargetLanguage { get; init; } = string.Empty;

    /// <summary>可选说明；null/纯空白按 null 提交。</summary>
    public string? Notes { get; init; }

    public bool CaseSensitive { get; init; }

    /// <summary>更新/删除使用的乐观并发版本；创建请求忽略。</summary>
    public int? Version { get; init; }
}
