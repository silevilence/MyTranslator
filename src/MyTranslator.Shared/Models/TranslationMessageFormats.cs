namespace MyTranslator.Contracts;

/// <summary>前后端共用的翻译消息格式目录；不依赖 UI 或执行层。</summary>
public static class TranslationMessageFormats
{
    public const string General = "general";
    public const string HyMt2 = "hy-mt2";
    public const string IndexTranslate = "bilibili-index-translate";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { General, HyMt2, IndexTranslate });
    public static bool IsSupported(string? value) => value is General or HyMt2 or IndexTranslate;
    public static bool IsSpecialized(string value) => value is HyMt2 or IndexTranslate;
    public static string ResourceKey(string value) => value switch
    {
        General => "FormatGeneral",
        HyMt2 => "FormatHyMt2",
        IndexTranslate => "FormatIndexTranslate",
        _ => "FormatUnknown"
    };
}
