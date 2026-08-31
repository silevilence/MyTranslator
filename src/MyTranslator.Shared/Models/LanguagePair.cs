namespace MyTranslator.Shared.Models;

/// <summary>
/// 源/目标语言对（BCP 47 标签）。历史翻译对比（docs/back/TM 接口约定.md §2.1）以及
/// 分段确认的语言对上下文都以「明确的语言对」为前提：两者必须均为非空，
/// 且规范化后不得相同（服务端规范化大小写并返回 `invalid_language_tag`）。
/// </summary>
public sealed record LanguagePair(string? SourceLanguage, string? TargetLanguage)
{
    /// <summary>两个标签均已提供（非空）时，对比/确认才可执行。</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SourceLanguage) && !string.IsNullOrWhiteSpace(TargetLanguage);

    /// <summary>规范化：去首尾空白、空串归 null；大小写交由服务端规范化。</summary>
    public static LanguagePair Normalize(string? sourceLanguage, string? targetLanguage) => new(
        string.IsNullOrWhiteSpace(sourceLanguage) ? null : sourceLanguage.Trim(),
        string.IsNullOrWhiteSpace(targetLanguage) ? null : targetLanguage.Trim());

    /// <summary>规范化后源/目标相同（大小写不敏感比较，与服务端语义一致）。</summary>
    public bool HasSameLanguages =>
        SourceLanguage is not null && TargetLanguage is not null &&
        string.Equals(SourceLanguage, TargetLanguage, StringComparison.OrdinalIgnoreCase);
}
