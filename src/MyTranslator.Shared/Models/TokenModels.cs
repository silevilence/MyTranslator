namespace MyTranslator.Shared.Models;

/// <summary>Token 列表只含前缀，不含完整凭据。</summary>
public sealed record ApiTokenSummary(Guid Id, string Name, string TokenPrefix, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
/// <summary>完整 Token 只在创建响应中返回，界面只在当前内存中短暂展示。</summary>
public sealed record CreatedApiToken(Guid Id, string Name, string Token);
