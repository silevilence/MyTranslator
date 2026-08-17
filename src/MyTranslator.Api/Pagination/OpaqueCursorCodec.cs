using System.Text;
using System.Text.Json;

namespace MyTranslator.Api.Pagination;

internal static class OpaqueCursorCodec
{
    public static string Encode<TPayload>(TPayload payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static TPayload Decode<TPayload>(string cursor)
    {
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<TPayload>(
                       Encoding.UTF8.GetString(Convert.FromBase64String(base64)))
                   ?? throw new OpaqueCursorCodecException();
        }
        catch (OpaqueCursorCodecException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            throw new OpaqueCursorCodecException(exception);
        }
    }
}

internal sealed class OpaqueCursorCodecException(Exception? innerException = null)
    : Exception("The opaque cursor payload is invalid.", innerException);
