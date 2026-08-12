using MudBlazor;

namespace MyTranslator.Shared.Theme;

/// <summary>
/// MyTranslator 共享主题定义（设计规范 §2）：亮/暗两套 palette、语义色映射、字体双栈与字号层级。
/// 页面与组件禁止散落硬编码色值/字号，一律经本主题注入。
/// </summary>
public static class MyTranslatorTheme
{
    /// <summary>UI 字体栈（设计规范 §3.1，顺序不可调整）。</summary>
    public static readonly string[] UiFontStack =
    [
        "Segoe UI",
        "Microsoft YaHei",
        "PingFang SC",
        "Noto Sans CJK SC",
        "Source Han Sans SC",
        "sans-serif",
    ];

    /// <summary>等宽字体栈（设计规范 §3.2，占位符/标记/分段 ID/差异文本使用）。</summary>
    public static readonly string[] MonoFontStack =
    [
        "Cascadia Code",
        "JetBrains Mono",
        "Consolas",
        "Courier New",
        "monospace",
    ];

    /// <summary>生成完整主题。每端唯一的 MudThemeProvider 使用本主题并绑定亮/暗模式。</summary>
    public static MudTheme Create()
    {
        return new MudTheme
        {
            PaletteLight = CreateLightPalette(),
            PaletteDark = CreateDarkPalette(),
            Typography = CreateTypography(),
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = "4px",
            },
        };
    }

    private static PaletteLight CreateLightPalette()
    {
        return new PaletteLight
        {
            Primary = "#2563EB",
            Secondary = "#64748B",
            Info = "#3B82F6",
            Success = "#22C55E",
            Warning = "#F59E0B",
            Error = "#DC2626",
            AppbarBackground = "#2563EB",
            AppbarText = "#FFFFFF",
        };
    }

    private static PaletteDark CreateDarkPalette()
    {
        return new PaletteDark
        {
            Primary = "#60A5FA",
            Secondary = "#94A3B8",
            Info = "#60A5FA",
            Success = "#4ADE80",
            Warning = "#FBBF24",
            Error = "#F87171",
            AppbarBackground = "#1E293B",
            AppbarText = "#F8FAFC",
        };
    }

    private static Typography CreateTypography()
    {
        return new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = UiFontStack,
                FontSize = ".875rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            H1 = new H1Typography
            {
                FontFamily = UiFontStack,
                FontSize = "1.25rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H2 = new H2Typography
            {
                FontFamily = UiFontStack,
                FontSize = "1rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H3 = new H3Typography
            {
                FontFamily = UiFontStack,
                FontSize = "1rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H4 = new H4Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".9375rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H5 = new H5Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".875rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H6 = new H6Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".875rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            Subtitle1 = new Subtitle1Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".875rem",
                FontWeight = "500",
                LineHeight = "1.5",
            },
            Subtitle2 = new Subtitle2Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".75rem",
                FontWeight = "500",
                LineHeight = "1.5",
            },
            Body1 = new Body1Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".875rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Body2 = new Body2Typography
            {
                FontFamily = UiFontStack,
                FontSize = ".75rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Button = new ButtonTypography
            {
                FontFamily = UiFontStack,
                FontSize = ".875rem",
                FontWeight = "600",
                LineHeight = "1.5",
            },
            Caption = new CaptionTypography
            {
                FontFamily = UiFontStack,
                FontSize = ".75rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Overline = new OverlineTypography
            {
                FontFamily = UiFontStack,
                FontSize = ".75rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = "normal",
            },
        };
    }
}
