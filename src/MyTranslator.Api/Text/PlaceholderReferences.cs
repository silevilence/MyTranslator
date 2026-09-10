using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyTranslator.Api.Text;

/// <summary>
/// 占位符引用一致性分析（《文件导入拆解与导出接口约定》§7、ADR-0002）。
/// TM 写入校验（<c>TranslationMemoryMarkup</c>）与硬性规则引擎（<c>PlaceholderIntegrityRule</c>）
/// 共用同一套「按标记表识别注册引用 + 按值比较计数」语义，避免两处实现各自漂移；
/// 各自的额外策略（TM 的成对嵌套校验、规则引擎的形似字面引用计数比较）由调用方叠加。
/// </summary>
internal static partial class PlaceholderReferences
{
    /// <summary>标记表声明：<c>paired</c> 消耗 <c>&lt;xN&gt;</c>/<c>&lt;/xN&gt;</c>，<c>standalone</c> 消耗 <c>&lt;xN/&gt;</c>。</summary>
    public readonly record struct Declaration(int Id, bool IsPaired);

    /// <summary>从标记表条目（形状见《文件导入拆解与导出接口约定》§7）读取声明。</summary>
    public static Declaration DeclarationOf(JsonElement markupItem) => new(
        markupItem.GetProperty("id").GetInt32(),
        markupItem.GetProperty("kind").GetString() == "paired");

    /// <summary>按声明顺序展开的必需引用序列。</summary>
    public static IReadOnlyList<string> RequiredReferences(IEnumerable<Declaration> declarations) =>
        declarations
            .SelectMany(declaration => declaration.IsPaired
                ? new[] { $"<x{declaration.Id}>", $"</x{declaration.Id}>" }
                : new[] { $"<x{declaration.Id}/>" })
            .ToArray();

    /// <summary>文本中与标记表注册项完全对应的引用，按出现顺序；其余形似文本按普通可译文本处理。</summary>
    public static IReadOnlyList<string> RegisteredReferences(string text, IReadOnlySet<string> registered) =>
        Pattern().Matches(text).Select(match => match.Value).Where(registered.Contains).ToArray();

    /// <summary>文本中形似引用但未被标记表注册的部分，按出现顺序。</summary>
    public static IReadOnlyList<string> UnregisteredReferences(string text, IReadOnlySet<string> registered) =>
        Pattern().Matches(text)
            .Select(match => match.Value)
            .Where(token => !registered.Contains(token))
            .ToArray();

    /// <summary>两组引用按值（种类 + 重数）比较，与顺序无关。</summary>
    public static bool HaveSameCounts(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftCounts = left.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rightCounts = right.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return leftCounts.Count == rightCounts.Count &&
               leftCounts.All(pair => rightCounts.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    [GeneratedRegex(@"</?x[1-9][0-9]*/?>", RegexOptions.CultureInvariant)]
    public static partial Regex Pattern();
}
