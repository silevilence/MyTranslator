using System.Text;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>占位符引用在段内文本中的位置种类。</summary>
public enum MarkupReferenceKind
{
    /// <summary>成对开始 `<xN>`。</summary>
    Open,

    /// <summary>成对结束 `</xN>`。</summary>
    Close,

    /// <summary>独立标记 `&lt;xN/&gt;`。</summary>
    Standalone,
}

/// <summary>
/// 分段文本的一部分：普通可译文本（<see cref="Item"/> 为 null）或占位符引用。
/// </summary>
/// <param name="Item">引用的标记表项；普通文本为 null。</param>
/// <param name="Kind">引用种类；普通文本为 null。</param>
/// <param name="Text">原始文本片段（含占位符字面量）。</param>
public sealed record MarkupPart(MarkupItem? Item, MarkupReferenceKind? Kind, string Text);

/// <summary>
/// 按 docs/back 文件导入拆解与导出接口约定 §7 解析分段文本中的占位符引用：
/// 只有能与该分段标记表同 ID、同种类记录对应的完整引用才是占位符；
/// 其他 `<xN>` 形状文本按普通可译字符处理。
/// </summary>
public static class MarkupReferenceParser
{
    /// <summary>将 <paramref name="text"/> 拆分为普通文本与占位符引用的有序列表。</summary>
    public static IReadOnlyList<MarkupPart> Parse(string text, IReadOnlyList<MarkupItem> markupTable)
    {
        var table = markupTable ?? [];
        var byId = table.Where(item => item.Id > 0).ToDictionary(item => item.Id);
        var parts = new List<MarkupPart>();
        var plain = new StringBuilder();

        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '<'
                && TryReadReference(text, index, byId, out var item, out var kind, out var consumed))
            {
                FlushPlainText();
                parts.Add(new MarkupPart(item, kind, text.Substring(index, consumed)));
                index += consumed;
            }
            else
            {
                plain.Append(text[index]);
                index++;
            }
        }

        FlushPlainText();
        return parts;

        void FlushPlainText()
        {
            if (plain.Length > 0)
            {
                parts.Add(new MarkupPart(null, null, plain.ToString()));
                plain.Clear();
            }
        }
    }

    private static bool TryReadReference(
        string text,
        int start,
        IReadOnlyDictionary<int, MarkupItem> byId,
        out MarkupItem? item,
        out MarkupReferenceKind? kind,
        out int consumed)
    {
        item = null;
        kind = null;
        consumed = 0;

        // 形式：`</xN>`（成对结束）、`<xN/>`（独立）、`<xN>`（成对开始）
        var isClosing = text[start] == '<' && start + 1 < text.Length && text[start + 1] == '/';
        var digitsStart = isClosing ? start + 3 : start + 2;
        if (digitsStart >= text.Length || !IsDigit(text[digitsStart]))
        {
            return false;
        }

        var id = 0;
        var cursor = digitsStart;
        while (cursor < text.Length && IsDigit(text[cursor]))
        {
            // 超过 int 范围的超长数字字面量不是占位符（契约 §7：按普通文本处理），不得溢出崩溃
            if (id > int.MaxValue / 10)
            {
                return false;
            }

            var digit = text[cursor] - '0';
            if (id == int.MaxValue / 10 && digit > int.MaxValue % 10)
            {
                return false;
            }

            id = id * 10 + digit;
            cursor++;
        }

        if (cursor >= text.Length)
        {
            return false;
        }

        MarkupReferenceKind candidateKind;
        if (isClosing)
        {
            if (text[cursor] != '>')
            {
                return false;
            }

            candidateKind = MarkupReferenceKind.Close;
            consumed = cursor - start + 1;
        }
        else if (cursor + 1 < text.Length && text[cursor] == '/' && text[cursor + 1] == '>')
        {
            candidateKind = MarkupReferenceKind.Standalone;
            consumed = cursor - start + 2;
        }
        else if (text[cursor] == '>')
        {
            candidateKind = MarkupReferenceKind.Open;
            consumed = cursor - start + 1;
        }
        else
        {
            return false;
        }

        if (!byId.TryGetValue(id, out var candidate))
        {
            return false;
        }

        // 种类必须与标记表一致：paired 对应 Open/Close，standalone 对应 Standalone
        var isPaired = string.Equals(candidate.Kind, "paired", StringComparison.Ordinal);
        var kindMatches = candidateKind == MarkupReferenceKind.Standalone ? !isPaired : isPaired;
        if (!kindMatches)
        {
            return false;
        }

        item = candidate;
        kind = candidateKind;
        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
