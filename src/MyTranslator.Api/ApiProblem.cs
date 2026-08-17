namespace MyTranslator.Api;

internal static class ApiProblem
{
    public static IResult Create(
        string code,
        string title,
        int statusCode,
        IReadOnlyDictionary<string, object?>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(
            statusCode: statusCode,
            title: title,
            type: $"urn:mytranslator:problem:{code.Replace('_', '-')}",
            extensions: extensions);
    }
}
