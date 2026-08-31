using MyTranslator.Shared.Components;
using MyTranslator.Shared.Models;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// DiffViewer 输入未变判定回归（审查标准维度五：每次渲染必要；同引用原地变更不漏检）。
/// </summary>
public class DiffViewerInputsTests
{
    private static readonly MarkupItem Markup = new()
    {
        Id = 1,
        Kind = "paired",
        OpeningText = "<strong>",
        ClosingText = "</strong>",
        Meaning = "加粗强调",
    };

    private static bool IsUnchanged(
        string current, IReadOnlyList<MarkupItem> currentMarkup,
        string reference, IReadOnlyList<MarkupItem> referenceMarkup,
        string cachedCurrent, IReadOnlyList<MarkupItem> cachedCurrentMarkup,
        string cachedReference, IReadOnlyList<MarkupItem> cachedReferenceMarkup,
        bool computed)
        => DiffViewer.InputsUnchanged(
            current, currentMarkup, reference, referenceMarkup,
            cachedCurrent, cachedCurrentMarkup, cachedReference, cachedReferenceMarkup, computed);

    [Fact]
    public void 未首次计算_判定为变化()
    {
        Assert.False(IsUnchanged("a", [], "b", [], string.Empty, [], string.Empty, [], computed: false));
    }

    [Fact]
    public void 全部一致_判定未变化()
    {
        Assert.True(IsUnchanged("a", [Markup], "b", [Markup], "a", [Markup], "b", [Markup], computed: true));
    }

    [Fact]
    public void 不同实例相同内容_按值判定未变化()
    {
        var fresh = new MarkupItem
        {
            Id = 1,
            Kind = "paired",
            OpeningText = "<strong>",
            ClosingText = "</strong>",
            Meaning = "加粗强调",
        };

        Assert.True(IsUnchanged("a", [fresh], "b", [fresh], "a", [Markup], "b", [Markup], computed: true));
    }

    [Fact]
    public void 同引用原地变更内容_判定为变化()
    {
        var mutated = new List<MarkupItem> { Markup };
        // 原地修改集合内容（替换项），引用不变
        mutated[0] = new MarkupItem { Id = 2, Kind = "standalone", OriginalText = "<hr/>", Meaning = "分隔线" };

        Assert.False(IsUnchanged("a", mutated, "b", mutated, "a", [Markup], "b", [Markup], computed: true));
    }

    [Fact]
    public void 任一文本变化_判定为变化()
    {
        Assert.False(IsUnchanged("a", [], "bX", [], "a", [], "b", [], computed: true));
        Assert.False(IsUnchanged("aX", [], "b", [], "a", [], "b", [], computed: true));
    }
}
