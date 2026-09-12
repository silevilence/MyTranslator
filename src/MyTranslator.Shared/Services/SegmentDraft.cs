using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>功能验证用编辑模型：标记为只读片段，只有间隙文本可编辑。正式界面可替换渲染而复用此保护边界。</summary>
public sealed class SegmentDraft
{
    public Segment Original { get; }
    public IReadOnlyList<DraftPart> Parts { get; }
    public string? TargetText => Parts.Where(part => part.Markup is null).All(part => string.IsNullOrWhiteSpace(part.Text))
        ? null : string.Concat(Parts.Select(part => part.Text));
    public bool IsDirty => TargetText != Original.TargetText;

    public SegmentDraft(Segment segment)
    {
        Original = segment;
        var parts = new List<DraftPart>();
        var parsed = MarkupReferenceParser.Parse(segment.TargetText ?? segment.SourceText ?? string.Empty, segment.MarkupTable);
        parts.Add(new(null, string.Empty));
        foreach (var part in parsed)
        {
            if (part.Item is not null)
            {
                parts.Add(new(part, part.Text));
                parts.Add(new(null, string.Empty));
            }
            else if (segment.TargetText is not null) parts[^1].SetText(parts[^1].Text + part.Text);
        }
        Parts = parts;
    }

    public void SetText(int index, string? text) => Parts[index].SetText(text ?? string.Empty);
}

/// <summary>占位符无法通过文本 setter 改写。</summary>
public sealed class DraftPart(MarkupPart? markup, string text)
{
    public MarkupPart? Markup { get; } = markup;
    public string Text { get; private set; } = text;
    internal void SetText(string text)
    {
        if (Markup is not null) throw new InvalidOperationException("Markup is read-only.");
        Text = text;
    }
}
