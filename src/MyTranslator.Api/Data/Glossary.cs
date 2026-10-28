namespace MyTranslator.Api.Data;

public sealed class Glossary
{
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000001");
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
}
