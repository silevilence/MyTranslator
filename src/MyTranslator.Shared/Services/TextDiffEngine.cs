using System.Text;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>逐字差异块种类。</summary>
public enum DiffChunkKind
{
    /// <summary>两侧相同的文本。</summary>
    Equal,

    /// <summary>仅当前译文存在的文本（相对参考译文新增）。</summary>
    Inserted,

    /// <summary>仅参考译文存在的文本（相对当前译文删除）。</summary>
    Removed,
}

/// <summary>
/// 一段连续的差异文本。
/// </summary>
/// <param name="Kind">块种类。</param>
/// <param name="Text">块文本。</param>
/// <param name="IsMarkup">该块是否为占位符引用（等宽样式，不得与普通文本合并）。</param>
/// <param name="TokenCount">块包含的 token 数（差异距离度量，供测试验证脚本最优性）。</param>
public sealed record DiffChunk(DiffChunkKind Kind, string Text, bool IsMarkup, int TokenCount);

/// <summary>差异分词：占位符引用为原子 token（不得拆分）；普通文本按 ASCII 单词/单字符切分。</summary>
/// <param name="Text">token 文本。</param>
/// <param name="IsMarkup">是否为占位符引用 token。</param>
public sealed record TextDiffToken(string Text, bool IsMarkup);

/// <summary>
/// 展示用逐字差异引擎（docs/back/TM 接口约定.md §7.1 / §9.1 第 3 步）。
/// 仅用于视觉呈现，不计算相似度或判定告警——数值与告警一律使用服务端返回结果。
///
/// 算法：Myers O(ND) 最短编辑脚本在 token 序列上求差；编辑距离超过
/// <see cref="MaxEditDistance"/> 时退化为整段替换（超长分段仅显示整体差异，成本有界）。
/// 占位符引用依据各自标记表识别（ADR-0002：字面形似引用按普通文本处理）。
/// </summary>
public static class TextDiffEngine
{
    /// <summary>编辑距离上限：超过后退化为整段替换，避免超长文本差分的耗时/内存无界。</summary>
    public const int MaxEditDistance = 1024;

    /// <summary>将文本拆分为差异 token：占位符引用整体为一个 token，普通文本按 ASCII 单词与单字符切分（CJK 逐字）。</summary>
    public static IReadOnlyList<TextDiffToken> Tokenize(string text, IReadOnlyList<MarkupItem> markupTable)
    {
        var tokens = new List<TextDiffToken>();
        foreach (var part in MarkupReferenceParser.Parse(text, markupTable))
        {
            if (part.Item is not null)
            {
                tokens.Add(new TextDiffToken(part.Text, IsMarkup: true));
                continue;
            }

            AppendPlainTokens(part.Text, tokens);
        }

        return tokens;
    }

    /// <summary>
    /// 计算当前译文与参考译文的逐字差异块序列。
    /// 两侧文本为空串时返回空序列；两侧完全相同时全部为 <see cref="DiffChunkKind.Equal"/>。
    /// </summary>
    /// <param name="currentText">当前（保存的）译文；null 按空串处理。</param>
    /// <param name="currentMarkupTable">当前译文对应的标记表。</param>
    /// <param name="referenceText">历史译文。</param>
    /// <param name="referenceMarkupTable">历史译文对应的标记表。</param>
    public static IReadOnlyList<DiffChunk> Compute(
        string? currentText,
        IReadOnlyList<MarkupItem> currentMarkupTable,
        string? referenceText,
        IReadOnlyList<MarkupItem> referenceMarkupTable)
    {
        var current = Tokenize(currentText ?? string.Empty, currentMarkupTable);
        var reference = Tokenize(referenceText ?? string.Empty, referenceMarkupTable);
        var ops = ShortestEditScript(current, reference);
        return MergeChunks(current, reference, ops);
    }

    /// <summary>把 token 列表中的普通文本段切分为 ASCII 单词（字母/数字连续）与单字符 token。</summary>
    private static void AppendPlainTokens(string text, List<TextDiffToken> tokens)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (IsWordChar(c))
            {
                word.Append(c);
            }
            else
            {
                if (word.Length > 0)
                {
                    tokens.Add(new TextDiffToken(word.ToString(), IsMarkup: false));
                    word.Clear();
                }

                tokens.Add(new TextDiffToken(c.ToString(), IsMarkup: false));
            }
        }

        if (word.Length > 0)
        {
            tokens.Add(new TextDiffToken(word.ToString(), IsMarkup: false));
        }
    }

    private static bool IsWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    /// <summary>
    /// Myers O(ND) 最短编辑脚本，返回按原顺序的 (Kind, AIndex, BIndex) 操作三元组；
    /// 编辑距离超过 <see cref="MaxEditDistance"/> 时返回整段替换。
    /// </summary>
    private static List<(DiffChunkKind Kind, int A, int B)> ShortestEditScript(
        IReadOnlyList<TextDiffToken> a,
        IReadOnlyList<TextDiffToken> b)
    {
        var ops = new List<(DiffChunkKind Kind, int A, int B)>();
        int n = a.Count, m = b.Count;

        if (n == 0)
        {
            for (var i = 0; i < m; i++)
            {
                ops.Add((DiffChunkKind.Inserted, -1, i));
            }

            return ops;
        }

        if (m == 0)
        {
            for (var i = 0; i < n; i++)
            {
                ops.Add((DiffChunkKind.Removed, i, -1));
            }

            return ops;
        }

        int maxD = Math.Min(n + m, MaxEditDistance);
        int offset = maxD;
        var v = new int[2 * maxD + 1];
        // trace[d] 保存处理完深度 d 后的 v 快照，用于回溯；v[offset + k] = 对角线上最远的 x
        var trace = new List<int[]>();

        int foundD = -1;
        for (var d = 0; d <= maxD; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    // 向下：消耗一个 b token（插入）
                    x = v[offset + k + 1];
                }
                else
                {
                    // 向右：消耗一个 a token（删除）
                    x = v[offset + k - 1] + 1;
                }

                var y = x - k;
                while (x < n && y < m && a[x].Text == b[y].Text)
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    foundD = d;
                    break;
                }
            }

            var snapshot = new int[v.Length];
            Array.Copy(v, snapshot, v.Length);
            trace.Add(snapshot);
            if (foundD >= 0)
            {
                break;
            }
        }

        if (foundD < 0)
        {
            // 编辑距离超过上限：整段替换（先全部删除，再全部插入）
            for (var i = 0; i < n; i++)
            {
                ops.Add((DiffChunkKind.Removed, i, -1));
            }

            for (var j = 0; j < m; j++)
            {
                ops.Add((DiffChunkKind.Inserted, -1, j));
            }

            return ops;
        }

        // 回溯：从终点 (n, m) 倒推每一步，操作按逆序记录
        var traceX = n;
        var traceY = m;
        for (var d = foundD; d > 0; d--)
        {
            var previous = trace[d - 1];
            var k = traceX - traceY;
            var fromDown = k == -d || (k != d && previous[offset + k - 1] < previous[offset + k + 1]);
            var previousK = fromDown ? k + 1 : k - 1;
            var previousX = previous[offset + previousK];
            var previousY = previousX - previousK;

            // 蛇形回退：共同前缀（倒序撤销，直到本步移动的起点）
            while (traceX > previousX && traceY > previousY)
            {
                ops.Add((DiffChunkKind.Equal, traceX - 1, traceY - 1));
                traceX--;
                traceY--;
            }

            if (fromDown)
            {
                // 本步向下：消耗 b[previousY]（插入）
                ops.Add((DiffChunkKind.Inserted, -1, previousY));
                traceY--;
            }
            else
            {
                // 本步向右：消耗 a[previousX]（删除）
                ops.Add((DiffChunkKind.Removed, previousX, -1));
                traceX--;
            }
        }

        // d = 0 的剩余共同前缀
        while (traceX > 0 && traceY > 0)
        {
            ops.Add((DiffChunkKind.Equal, traceX - 1, traceY - 1));
            traceX--;
            traceY--;
        }

        ops.Reverse();
        return ops;
    }

    /// <summary>把操作三元组合并为连续文本块；占位符引用与普通文本不跨类合并。</summary>
    private static IReadOnlyList<DiffChunk> MergeChunks(
        IReadOnlyList<TextDiffToken> a,
        IReadOnlyList<TextDiffToken> b,
        List<(DiffChunkKind Kind, int A, int B)> ops)
    {
        var chunks = new List<DiffChunk>();
        foreach (var (kind, aIndex, bIndex) in ops)
        {
            var token = kind switch
            {
                DiffChunkKind.Inserted => b[bIndex],
                _ => a[aIndex],
            };

            if (chunks.Count > 0
                && chunks[^1].Kind == kind
                && chunks[^1].IsMarkup == token.IsMarkup)
            {
                chunks[^1] = chunks[^1] with { Text = chunks[^1].Text + token.Text, TokenCount = chunks[^1].TokenCount + 1 };
            }
            else
            {
                chunks.Add(new DiffChunk(kind, token.Text, token.IsMarkup, TokenCount: 1));
            }
        }

        return chunks;
    }
}
