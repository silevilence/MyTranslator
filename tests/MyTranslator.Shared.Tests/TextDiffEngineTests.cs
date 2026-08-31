using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 逐字差异引擎回归（docs/back/TM 接口约定.md §9.1 第 3 步：视觉差异展示）。
/// 引擎仅负责展示差分，不计算相似度——该断言约束的是脚本结构正确性与最优性。
/// </summary>
public class TextDiffEngineTests
{
    private static readonly MarkupItem[] EmptyMarkup = [];

    private static MarkupItem PairedMarkup() =>
        new() { Id = 1, Kind = "paired", OpeningText = "<strong>", ClosingText = "</strong>", Meaning = "加粗强调" };

    private static IReadOnlyList<DiffChunk> Compute(string current, string reference) =>
        TextDiffEngine.Compute(current, EmptyMarkup, reference, EmptyMarkup);

    private static string ReconstructA(IEnumerable<DiffChunk> chunks) =>
        string.Concat(chunks.Where(c => c.Kind != DiffChunkKind.Inserted).Select(c => c.Text));

    private static string ReconstructB(IEnumerable<DiffChunk> chunks) =>
        string.Concat(chunks.Where(c => c.Kind != DiffChunkKind.Removed).Select(c => c.Text));

    private static int DiffCost(IEnumerable<DiffChunk> chunks) =>
        chunks.Where(c => c.Kind != DiffChunkKind.Equal).Sum(c => c.TokenCount);

    [Fact]
    public void 相同文本_全部Equal()
    {
        var chunks = Compute("Use translation memory.", "Use translation memory.");

        var chunk = Assert.Single(chunks);
        Assert.Equal(DiffChunkKind.Equal, chunk.Kind);
        Assert.Equal("Use translation memory.", chunk.Text);
        Assert.False(chunk.IsMarkup);
    }

    [Fact]
    public void 当前较短_末尾插入()
    {
        var chunks = Compute("使用翻译", "使用翻译记忆库");

        Assert.Equal(DiffChunkKind.Equal, chunks[0].Kind);
        Assert.Equal("使用翻译", chunks[0].Text);
        Assert.Equal(DiffChunkKind.Inserted, chunks[1].Kind);
        Assert.Equal("记忆库", chunks[1].Text);
    }

    [Fact]
    public void 参考较长_末尾删除()
    {
        var chunks = Compute("使用翻译记忆库", "使用翻译");

        Assert.Equal(DiffChunkKind.Equal, chunks[0].Kind);
        Assert.Equal(DiffChunkKind.Removed, chunks[1].Kind);
        Assert.Equal("记忆库", chunks[1].Text);
    }

    [Fact]
    public void 单词修改_删除加插入()
    {
        var chunks = Compute("Use translation memory.", "Use translation history.");

        Assert.Equal(new[] { DiffChunkKind.Equal, DiffChunkKind.Removed, DiffChunkKind.Inserted, DiffChunkKind.Equal },
            chunks.Select(c => c.Kind).ToArray());
        Assert.Equal("Use translation ", chunks[0].Text);
        Assert.Equal("memory", chunks[1].Text);
        Assert.Equal("history", chunks[2].Text);
    }

    [Fact]
    public void CJK逐字差异()
    {
        var chunks = Compute("使用翻译内存库", "使用翻译记忆库");

        Assert.Equal(new[] { DiffChunkKind.Equal, DiffChunkKind.Removed, DiffChunkKind.Inserted, DiffChunkKind.Equal },
            chunks.Select(c => c.Kind).ToArray());
        Assert.Equal("内存", chunks[1].Text);
        Assert.Equal("记忆", chunks[2].Text);
    }

    [Fact]
    public void 占位符引用_作为原子token且不拆分()
    {
        var markup = PairedMarkup();
        var chunks = TextDiffEngine.Compute("Use <x1>TM</x1>.", [markup], "使用<x1>TM库</x1>。", [markup]);

        // 重构校验：非插入块拼接 == 当前文本；非删除块拼接 == 参考文本
        Assert.Equal("Use <x1>TM</x1>.", ReconstructA(chunks));
        Assert.Equal("使用<x1>TM库</x1>。", ReconstructB(chunks));

        // 占位符引用整体标记，且不拆分：Equal "<x1>" / "</x1>" 各为一等宽块
        var markupEquals = chunks.Where(c => c.IsMarkup).Select(c => c.Text).ToArray();
        Assert.Contains("<x1>", markupEquals);
        Assert.Contains("</x1>", markupEquals);

        // 普通文本块不得假装为标记（不包含 '<'）
        Assert.DoesNotContain(chunks, c => !c.IsMarkup && c.Text.Contains('<'));
    }

    [Fact]
    public void 字面形似引用_无标记表时按普通文本()
    {
        var chunks = Compute("a<x3>b", "a<x3>c");

        Assert.Equal(new[] { DiffChunkKind.Equal, DiffChunkKind.Removed, DiffChunkKind.Inserted },
            chunks.Select(c => c.Kind).ToArray());
        Assert.Equal("a<x3>", chunks[0].Text);
        Assert.False(chunks[0].IsMarkup);
    }

    [Fact]
    public void 空侧_整体插入或删除()
    {
        var inserted = Compute("", "abc");
        var removed = Compute("abc", "");

        Assert.Equal(DiffChunkKind.Inserted, Assert.Single(inserted).Kind);
        Assert.Equal("abc", Assert.Single(inserted).Text);
        Assert.Equal(DiffChunkKind.Removed, Assert.Single(removed).Kind);
        Assert.Empty(Compute("", ""));
    }

    [Fact]
    public void 超限退化_整段替换()
    {
        var a = Enumerable.Range(0, 1300).Select(i => $"t{i}").ToArray();
        var b = Enumerable.Range(0, 1300).Select(i => $"u{i}").ToArray();
        var chunks = TextDiffEngine.Compute(string.Join(" ", a), EmptyMarkup, string.Join(" ", b), EmptyMarkup);
        var aTokens = TextDiffEngine.Tokenize(string.Join(" ", a), EmptyMarkup).Count;
        var bTokens = TextDiffEngine.Tokenize(string.Join(" ", b), EmptyMarkup).Count;

        Assert.Equal(2, chunks.Count);
        Assert.Equal(DiffChunkKind.Removed, chunks[0].Kind);
        Assert.Equal(DiffChunkKind.Inserted, chunks[1].Kind);
        Assert.Equal(aTokens, chunks[0].TokenCount);
        Assert.Equal(bTokens, chunks[1].TokenCount);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("abc", "")]
    [InlineData("", "abc")]
    [InlineData("使用翻译记忆库", "使用翻译缓存库")]
    [InlineData("Use <x1>TM</x1>.", "使用<x1>TM</x1>。")]
    public void 脚本重构_两侧完整还原(string current, string reference)
    {
        var markup = PairedMarkup();
        var chunks = TextDiffEngine.Compute(current, [markup], reference, [markup]);

        Assert.Equal(current, ReconstructA(chunks));
        Assert.Equal(reference, ReconstructB(chunks));
    }

    [Fact]
    public void 随机输入_脚本最优且完整还原()
    {
        // 对照实现：token 级 DP LCS 求最小编辑距离，验证 Myers 脚本最优性
        var random = new Random(42);
        const string alphabet = "abc .汉译";
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var current = RandomText(random, alphabet);
            var reference = RandomText(random, alphabet);
            var currentTokens = TextDiffEngine.Tokenize(current, EmptyMarkup);
            var referenceTokens = TextDiffEngine.Tokenize(reference, EmptyMarkup);

            var chunks = Compute(current, reference);

            Assert.Equal(current, ReconstructA(chunks));
            Assert.Equal(reference, ReconstructB(chunks));

            var lcs = LcsCount(
                currentTokens.Select(t => t.Text).ToList(),
                referenceTokens.Select(t => t.Text).ToList());
            var expectedCost = currentTokens.Count + referenceTokens.Count - 2 * lcs;
            Assert.Equal(expectedCost, DiffCost(chunks));
        }
    }

    private static string RandomText(Random random, string alphabet)
    {
        var length = random.Next(0, 15);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    private static int LcsCount(List<string> a, List<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        return dp[a.Count, b.Count];
    }
}
