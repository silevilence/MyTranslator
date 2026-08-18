namespace MyTranslator.Shared.Models;

/// <summary>
/// AI 提供商（docs/back/AI 配置管理接口约定.md §3.1）。
/// 二级配置的上级：连接器 kind、BaseUrl、密钥（仅掩码回显）、启用状态与运行参数。
/// </summary>
public sealed record AiProvider
{
    public Guid Id { get; init; }

    /// <summary>展示名；允许重名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>连接器类型：`openai` / `ollama`；调用方须容忍未来新增未知值。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>LLM API 基础地址；null 表示未配置（发起翻译时 503）。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>掩码密钥（如 `sk-***cdef`）；仅响应字段，请求不得携带。</summary>
    public string? ApiKeyMasked { get; init; }

    /// <summary>启用状态；已停用提供商被显式选择时创建运行返回 422。</summary>
    public bool Enabled { get; init; }

    /// <summary>全局唯一默认提供商；与其默认模型合成默认对。</summary>
    public bool IsDefault { get; init; }

    /// <summary>每批分段数；null 表示未显式配置（服务端默认 20）。</summary>
    public int? BatchSize { get; init; }

    /// <summary>单次 LLM 请求超时（TimeSpan 常量格式，如 `00:01:00`）。</summary>
    public string? RequestTimeout { get; init; }

    /// <summary>每批最大尝试次数；null 表示未显式配置（服务端默认 3）。</summary>
    public int? MaxAttempts { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>内嵌模型列表（仅 GET /api/providers 响应；单资源不含）。</summary>
    public IReadOnlyList<AiModel> Models { get; init; } = [];
}

/// <summary>
/// AI 模型条目（docs/back/AI 配置管理接口约定.md §3.2）。
/// 挂于提供商下：厂商模型 ID、展示名与能力元数据（能力不进入翻译运行请求）。
/// </summary>
public sealed record AiModel
{
    /// <summary>模型条目 ID（翻译运行请求的 `modelId` 即此 ID）。</summary>
    public Guid Id { get; init; }

    public Guid ProviderId { get; init; }

    /// <summary>厂商模型 ID（对接提供商 API 的模型名），同一提供商内唯一。</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>展示名。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>能力声明：思考。</summary>
    public bool SupportsThinking { get; init; }

    /// <summary>能力声明：工具使用。</summary>
    public bool SupportsToolUse { get; init; }

    /// <summary>能力声明：流式。</summary>
    public bool SupportsStreaming { get; init; }

    /// <summary>提供商内唯一默认模型。</summary>
    public bool IsDefault { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 提供商创建/更新请求体（§4）。PUT 为全字段替换，<see cref="ApiKey"/> 例外：
/// null、缺省或掩码格式值均视为「保持原密钥」，其余非空值替换为新明文。
/// 不得携带响应字段 <c>apiKeyMasked</c>。
/// </summary>
public sealed record AiProviderUpsert
{
    /// <summary>展示名；非空。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>连接器类型：`openai` / `ollama`。</summary>
    public string Kind { get; init; } = "openai";

    /// <summary>LLM API 基础地址；null/空表示未配置。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>null/空白表示保持原密钥（更新时）；创建时表示不设置密钥。</summary>
    public string? ApiKey { get; init; }

    public bool Enabled { get; init; } = true;

    public bool IsDefault { get; init; }

    public int? BatchSize { get; init; }

    public string? RequestTimeout { get; init; }

    public int? MaxAttempts { get; init; }
}

/// <summary>模型创建/更新请求体（§5）。PUT 为全字段替换，无密钥类例外。</summary>
public sealed record AiModelUpsert
{
    /// <summary>厂商模型 ID；同一提供商内唯一。</summary>
    public string ModelId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool SupportsThinking { get; init; }

    public bool SupportsToolUse { get; init; }

    public bool SupportsStreaming { get; init; }

    public bool IsDefault { get; init; }
}

/// <summary>
/// 提供商列表的默认对查询（配置约定 §2.2：默认提供商 + 其默认模型合成默认对）。
/// 设置页与翻译面板共用，避免各组件重复同形判定。
/// </summary>
public static class AiProviderQueries
{
    /// <summary>是否存在完整默认对（默认提供商且其下存在默认模型）。</summary>
    public static bool HasDefaultPair(this IReadOnlyList<AiProvider>? providers) =>
        DefaultProvider(providers)?.Models.Any(m => m.IsDefault) == true;

    /// <summary>全局唯一默认提供商；无默认提供商时为 null。</summary>
    public static AiProvider? DefaultProvider(this IReadOnlyList<AiProvider>? providers) =>
        providers?.FirstOrDefault(p => p.IsDefault);

    /// <summary>提供商内默认模型；无时为 null。</summary>
    public static AiModel? DefaultModel(this AiProvider? provider) =>
        provider?.Models.FirstOrDefault(m => m.IsDefault);
}
