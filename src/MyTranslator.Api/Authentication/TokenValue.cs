using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MyTranslator.Api.Authentication;

public static partial class TokenValue
{
    public static string Generate() => $"sk-{Guid.NewGuid():N}";

    public static bool IsValid(string value, bool allowDevelopmentToken) =>
        StandardTokenPattern().IsMatch(value) ||
        (allowDevelopmentToken && DevelopmentTokenPattern().IsMatch(value));

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string GetDisplayPrefix(string value) => value[..Math.Min(value.Length, 15)];

    [GeneratedRegex("^sk-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex StandardTokenPattern();

    [GeneratedRegex("^sk-dev-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DevelopmentTokenPattern();
}
