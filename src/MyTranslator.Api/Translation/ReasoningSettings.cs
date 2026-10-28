using Microsoft.Extensions.AI;

namespace MyTranslator.Api.Translation;

internal static class ReasoningSettings
{
    public static ReasoningOptions? Create(string? value) => value is null ? null : new()
    {
        Effort = value switch
        {
            "none" => ReasoningEffort.None, "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium, "high" => ReasoningEffort.High,
            "xhigh" => ReasoningEffort.ExtraHigh,
            _ => throw new InvalidOperationException("Invalid persisted reasoning effort.")
        }
    };

    public static string Background(string? value) => string.IsNullOrWhiteSpace(value) ? "" :
        "\nUse the following background as translation/review reference; keep all output and placeholder requirements.\n【背景信息】\n" + value;
}
