using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class MarkupReferenceParserTests
{
    private static MarkupItem Paired(int id, string? meaning = null) => new()
    {
        Id = id,
        Kind = "paired",
        OpeningText = $"<{TagName(id)}>",
        ClosingText = $"</{TagName(id)}>",
        Meaning = meaning,
    };

    private static MarkupItem Standalone(int id, string? original = null, string? meaning = null) => new()
    {
        Id = id,
        Kind = "standalone",
        OriginalText = original ?? $"&entity{id};",
        Meaning = meaning,
    };

    private static string TagName(int id) => $"tag{id}";

    [Fact]
    public void Parse_纯文本_返回单个文本片段()
    {
        var parts = MarkupReferenceParser.Parse("Hello world", []);

        var part = Assert.Single(parts);
        Assert.Null(part.Item);
        Assert.Equal("Hello world", part.Text);
    }

    [Fact]
    public void Parse_空文本_返回空列表()
    {
        Assert.Empty(MarkupReferenceParser.Parse(string.Empty, []));
    }

    [Fact]
    public void Parse_成对与独立引用_按顺序拆分()
    {
        var table = new[] { Paired(7), Standalone(12, "&nbsp;") };

        var parts = MarkupReferenceParser.Parse("<x7>Read<x12/>this</x7>", table);

        Assert.Equal(5, parts.Count);
        AssertReference(parts[0], 7, MarkupReferenceKind.Open, "<x7>");
        AssertText(parts[1], "Read");
        AssertReference(parts[2], 12, MarkupReferenceKind.Standalone, "<x12/>");
        AssertText(parts[3], "this");
        AssertReference(parts[4], 7, MarkupReferenceKind.Close, "</x7>");
    }

    [Fact]
    public void Parse_多位数ID_正确解析()
    {
        var table = new[] { Paired(123) };

        var parts = MarkupReferenceParser.Parse("<x123>内容</x123>", table);

        Assert.Equal(3, parts.Count);
        AssertReference(parts[0], 123, MarkupReferenceKind.Open, "<x123>");
        AssertReference(parts[2], 123, MarkupReferenceKind.Close, "</x123>");
    }

    [Fact]
    public void Parse_不在标记表中的xN形状_按普通文本处理()
    {
        var parts = MarkupReferenceParser.Parse("字面量 <x1> 不是标记", [Paired(2)]);

        var part = Assert.Single(parts);
        Assert.Null(part.Item);
        Assert.Equal("字面量 <x1> 不是标记", part.Text);
    }

    [Fact]
    public void Parse_种类与标记表不一致_按普通文本处理()
    {
        // id=2 是 standalone：<x2> 与 </x2> 形状不是占位符
        var standaloneOnly = new[] { Standalone(2) };
        var openAsText = MarkupReferenceParser.Parse("<x2>开</x2>", standaloneOnly);
        Assert.Single(openAsText);
        Assert.Equal("<x2>开</x2>", openAsText[0].Text);

        // id=3 是 paired：<x3/> 自闭合形状不是占位符
        var pairedOnly = new[] { Paired(3) };
        var selfClosingAsText = MarkupReferenceParser.Parse("a<x3/>b", pairedOnly);
        Assert.Single(selfClosingAsText);
        Assert.Equal("a<x3/>b", selfClosingAsText[0].Text);
    }

    [Fact]
    public void Parse_不完整引用_按普通文本处理()
    {
        var table = new[] { Paired(5) };

        Assert.Equal("<x5", Assert.Single(MarkupReferenceParser.Parse("<x5", table)).Text);
        Assert.Equal("<x5/", Assert.Single(MarkupReferenceParser.Parse("<x5/", table)).Text);
        Assert.Equal("x5>", Assert.Single(MarkupReferenceParser.Parse("x5>", table)).Text);
    }

    [Fact]
    public void Parse_独立引用_标记表携带原文与含义()
    {
        var table = new[] { Standalone(12, "&nbsp;", "不间断空格") };

        var parts = MarkupReferenceParser.Parse("a<x12/>b", table);

        Assert.Equal(3, parts.Count);
        var reference = parts[1];
        Assert.Equal(12, reference.Item!.Id);
        Assert.Equal(MarkupReferenceKind.Standalone, reference.Kind);
        Assert.Equal("&nbsp;", reference.Item.OriginalText);
        Assert.Equal("不间断空格", reference.Item.Meaning);
    }

    [Fact]
    public void Parse_成对引用_标记表携带开闭原文()
    {
        var table = new[] { Paired(7) };

        var parts = MarkupReferenceParser.Parse("<x7>强调</x7>", table);

        Assert.Equal(MarkupReferenceKind.Open, parts[0].Kind);
        Assert.Equal("<tag7>", parts[0].Item!.OpeningText);
        Assert.Equal(MarkupReferenceKind.Close, parts[2].Kind);
        Assert.Equal("</tag7>", parts[2].Item!.ClosingText);
    }

    [Fact]
    public void Parse_超长数字字面量_不溢出并按普通文本处理()
    {
        // <x + 超过 int 范围的数字（如时间戳/代码片段）不是占位符，不得抛出 OverflowException
        var table = new[] { Paired(7) };

        var parts = MarkupReferenceParser.Parse("时间 <x20260813090000> 戳", table);

        var part = Assert.Single(parts);
        Assert.Null(part.Item);
        Assert.Equal("时间 <x20260813090000> 戳", part.Text);
    }

    [Fact]
    public void Parse_相邻引用与文本_不吞并字符()
    {
        var table = new[] { Paired(1), Paired(2) };

        var parts = MarkupReferenceParser.Parse("<x1><x2>中</x2></x1>", table);

        Assert.Equal(5, parts.Count);
        AssertReference(parts[0], 1, MarkupReferenceKind.Open, "<x1>");
        AssertReference(parts[1], 2, MarkupReferenceKind.Open, "<x2>");
        AssertText(parts[2], "中");
        AssertReference(parts[3], 2, MarkupReferenceKind.Close, "</x2>");
        AssertReference(parts[4], 1, MarkupReferenceKind.Close, "</x1>");
    }

    private static void AssertText(MarkupPart part, string expected)
    {
        Assert.Null(part.Item);
        Assert.Equal(expected, part.Text);
    }

    private static void AssertReference(
        MarkupPart part,
        int expectedId,
        MarkupReferenceKind expectedKind,
        string expectedText)
    {
        Assert.NotNull(part.Item);
        Assert.Equal(expectedId, part.Item.Id);
        Assert.Equal(expectedKind, part.Kind);
        Assert.Equal(expectedText, part.Text);
    }
}
