using System.Text;
using System.Text.RegularExpressions;
using MyTranslator.Api.Data;
using MyTranslator.Api.Terms;
using MyTranslator.Contracts;

namespace MyTranslator.Api.Translation;

/// <summary>专用单分段协议的材料、预算与回显防线；不负责运行状态或网络。</summary>
internal sealed record SpecializedTranslationPrompt(string Text, string Source, string Format, IReadOnlyList<Term> Terms)
{
    public const int OutputTokens = 4096;
    public const int RequestBudget = 16384;
    private const int TermBudget = 1600;
    private const string PlaceholderConstraint = "逐字保留所有 <xN>、</xN> 和 <xN/> 占位符的数量、编号、顺序、配对和嵌套，不要翻译或改写占位符。";

    public bool FitsBudget => (long)Encoding.UTF8.GetByteCount(Text) + 64 + OutputTokens <= RequestBudget;

    public static SpecializedTranslationPrompt Build(string format, string? sourceLanguage, string targetLanguage,
        string source, string markupTableJson, string fileType, IReadOnlyList<Term> candidates, string? backgroundInformation = null)
    {
        var targetName = TranslationLanguageNames.Find(targetLanguage)
            ?? throw new TranslationExecutionException("unsupported_language_pair", false, "Unknown target language name.");
        var sourceName = sourceLanguage is null ? "" : TranslationLanguageNames.Find(sourceLanguage)
            ?? throw new TranslationExecutionException("unsupported_language_pair", false, "Unknown source language name.");
        var separator = format == TranslationMessageFormats.HyMt2 ? " 翻译成 " : "→";
        var terms = new List<Term>();
        var lines = new List<string>();
        var termSize = 0;
        foreach (var term in candidates.OrderBy(term => term.Id))
        {
            if (string.IsNullOrWhiteSpace(term.SourceTerm) || string.IsNullOrWhiteSpace(term.TargetTerm) ||
                TermAlignmentMatcher.CountSourceOccurrences(source, markupTableJson, term.SourceTerm, term.CaseSensitive) == 0)
                continue;
            var line = term.SourceTerm + separator + term.TargetTerm;
            if (termSize + line.Length + 1 > TermBudget) continue;
            terms.Add(term);
            lines.Add(line);
            termSize += line.Length + 1;
        }

        var constraints = new List<string> { PlaceholderConstraint };
        if (fileType is "srt" or "vtt")
            constraints.Add("当前源文是一个完整字幕条目。保留条目内多行正文，不增加空行、时间轴、序号、标签、实体或 -->。");
        string prompt;
        if (format == TranslationMessageFormats.HyMt2)
        {
            var glossary = lines.Count == 0 ? "" : $"参考下面的翻译：\n{string.Join('\n', lines)}\n\n";
            var background = string.IsNullOrWhiteSpace(backgroundInformation) ? "" : $"【背景信息】\n{backgroundInformation}\n\n";
            var sourceLabel = background.Length == 0 ? "" : "【待翻译文本】\n";
            prompt = $"{background}{glossary}{string.Join('\n', constraints)}\n将以下文本翻译为{targetName}，注意只需要输出翻译后的结果，不要额外解释：\n{sourceLabel}{source}";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(backgroundInformation)) constraints.Add($"背景参考信息：{backgroundInformation}");
            if (lines.Count > 0) constraints.Insert(0, $"专名/术语对照: {string.Join('、', lines)}");
            prompt = $"请将以下{sourceName}文本翻译成{targetName}，并且严格遵循所有约束要求。\n\n【源文】\n{source}\n\n【约束要求】\n" +
                string.Join('\n', constraints.Select((constraint, index) => $"{index + 1}. 【硬性要求】{constraint}")) +
                "\n\n只输出译文，不要有任何额外说明。";
        }
        return new SpecializedTranslationPrompt(prompt, source, format, terms);
    }

    public string Validate(string response)
    {
        var text = response.Trim();
        if (Format == TranslationMessageFormats.HyMt2)
        {
            foreach (var label in new[] { "〖待翻译文本〗", "【待翻译文本】" })
                if (text.StartsWith(label, StringComparison.Ordinal) && !Source.Contains(label, StringComparison.Ordinal))
                {
                    text = text[label.Length..].Trim();
                    break;
                }
        }
        if (string.IsNullOrWhiteSpace(text))
            throw new TranslationExecutionException("llm_response_invalid", true, "Empty translation.");

        string[] markers = Format == TranslationMessageFormats.HyMt2
            ? ["参考下面的翻译：", "〖待翻译文本〗", "【待翻译文本】", "〖背景信息〗", "【背景信息】", "注意只需要输出翻译后的结果", PlaceholderConstraint]
            : ["【源文】", "【约束要求】", "【硬性要求】", "【注意】", "背景参考信息：", "专名/术语对照:", "只输出译文，不要有任何额外说明", "严格遵循所有约束要求"];
        if (markers.Any(marker => text.Contains(marker, StringComparison.Ordinal) && !Source.Contains(marker, StringComparison.Ordinal)) ||
            EchoesTerms(text))
            throw new TranslationExecutionException("llm_prompt_echo", true, "Translation echoed prompt materials.");
        return text;
    }

    private bool EchoesTerms(string response)
    {
        static string Compact(string value) => Regex.Replace(
            Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", ""), @"(?:[-=]+>|[⇒➜⟶]|翻译成)", "→");
        var output = Compact(response);
        if (!output.Contains('→')) return false;
        var source = Compact(Source);
        return Terms.Any(term =>
        {
            var from = Compact(term.SourceTerm);
            var to = Compact(term.TargetTerm);
            var pairs = new[] { $"{from}→{to}", $"{to}→{to}" };
            return !source.Contains($"{from}→{from}", StringComparison.Ordinal) &&
                !pairs.Any(pair => source.Contains(pair, StringComparison.Ordinal)) &&
                pairs.Any(pair => output.Contains(pair, StringComparison.Ordinal));
        });
    }
}
