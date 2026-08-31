using MyTranslator.Shared.Models;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 语言对值类型回归：规范化、完整性判定与相同语言判定（docs/back/TM 接口约定.md §2.1）。
/// </summary>
public class LanguagePairTests
{
    [Theory]
    [InlineData("en", "zh-CN", true)]
    [InlineData(" en ", "zh-CN", true)]
    [InlineData("", "zh-CN", false)]
    [InlineData(null, "zh-CN", false)]
    [InlineData("en", "", false)]
    public void IsComplete_仅当双语言非空(string? source, string? target, bool expected)
    {
        Assert.Equal(expected, LanguagePair.Normalize(source, target).IsComplete);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "zh-CN")]
    [InlineData(" en ", null)]
    public void Normalize_空串归null并去空白(string? source, string? target)
    {
        var pair = LanguagePair.Normalize(source, target);

        Assert.Equal(source is null or "" ? null : source.Trim(), pair.SourceLanguage);
        Assert.Equal(target is null or "" ? null : target.Trim(), pair.TargetLanguage);
    }

    [Fact]
    public void HasSameLanguages_大小写不敏感比较()
    {
        Assert.True(LanguagePair.Normalize("EN", "en").HasSameLanguages);
        Assert.True(LanguagePair.Normalize("zh-CN", "zh-cn").HasSameLanguages);
        Assert.False(LanguagePair.Normalize("en", "zh-CN").HasSameLanguages);
        Assert.False(LanguagePair.Normalize("en", null).HasSameLanguages);
        Assert.False(LanguagePair.Normalize(null, null).HasSameLanguages);
    }
}
