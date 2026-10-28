namespace MyTranslator.Shared.Models;

public sealed record Glossary(Guid Id, string Name, int Version)
{
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000001");
}
public sealed record GlossaryPage(IReadOnlyList<Glossary> Items, string? NextCursor);
public sealed record TaskReference(Guid? GlossaryId, string? BackgroundInformation, int Version);
