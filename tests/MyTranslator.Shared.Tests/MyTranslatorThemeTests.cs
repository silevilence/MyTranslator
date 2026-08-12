using MyTranslator.Shared.Theme;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class MyTranslatorThemeTests
{
    private readonly MudBlazor.MudTheme _theme = MyTranslatorTheme.Create();

    [Fact]
    public void Create_亮色主色与语义色符合设计规范()
    {
        Assert.Equal("#2563eb", _theme.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#3b82f6", _theme.PaletteLight.Info.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#22c55e", _theme.PaletteLight.Success.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#f59e0b", _theme.PaletteLight.Warning.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#dc2626", _theme.PaletteLight.Error.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
    }

    [Fact]
    public void Create_暗色语义色与亮色一一对应()
    {
        Assert.Equal("#60a5fa", _theme.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#4ade80", _theme.PaletteDark.Success.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#fbbf24", _theme.PaletteDark.Warning.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("#f87171", _theme.PaletteDark.Error.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
    }

    [Fact]
    public void Create_UI字体栈符合设计规范顺序()
    {
        Assert.Equal(
            ["Segoe UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC", "sans-serif"],
            _theme.Typography.Default.FontFamily ?? []);
    }

    [Fact]
    public void Create_字号层级符合设计规范()
    {
        Assert.Equal("1.25rem", _theme.Typography.H1.FontSize); // H1 20px / 600
        Assert.Equal("600", _theme.Typography.H1.FontWeight);
        Assert.Equal("1rem", _theme.Typography.H2.FontSize);    // H2 16px / 600
        Assert.Equal("600", _theme.Typography.H2.FontWeight);
        Assert.Equal(".875rem", _theme.Typography.Body1.FontSize); // 正文 14px
        Assert.Equal(".75rem", _theme.Typography.Body2.FontSize);  // 辅助 12px
    }

    [Fact]
    public void Create_等宽字体栈已声明()
    {
        Assert.Equal(
            ["Cascadia Code", "JetBrains Mono", "Consolas", "Courier New", "monospace"],
            MyTranslatorTheme.MonoFontStack);
    }

    [Fact]
    public void Create_圆角为4px网格()
    {
        Assert.Equal("4px", _theme.LayoutProperties.DefaultBorderRadius);
    }
}
