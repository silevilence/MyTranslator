using Microsoft.AspNetCore.Mvc;

namespace MyTranslator.Api;

internal static class ApiProblem
{
    public static IResult Create(
        string? code,
        string title,
        int statusCode,
        IReadOnlyDictionary<string, object?>? errors = null)
    {
        var extensions = CreateExtensions(code, errors);

        return Results.Problem(
            statusCode: statusCode,
            title: title,
            type: CreateType(code, statusCode),
            extensions: extensions);
    }

    public static ProblemDetails CreateDetails(
        HttpContext httpContext,
        string? code,
        string title,
        int statusCode,
        IReadOnlyDictionary<string, object?>? errors = null)
    {
        var details = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = CreateType(code, statusCode),
            Instance = httpContext.Request.Path
        };
        foreach (var extension in CreateExtensions(code, errors))
        {
            details.Extensions[extension.Key] = extension.Value;
        }

        return details;
    }

    private static Dictionary<string, object?> CreateExtensions(
        string? code,
        IReadOnlyDictionary<string, object?>? errors)
    {
        var extensions = new Dictionary<string, object?>();
        if (code is not null)
        {
            extensions["code"] = code;
        }

        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return extensions;
    }

    private static string CreateType(string? code, int statusCode) => code is null
        ? $"https://httpstatuses.com/{statusCode}"
        : $"urn:mytranslator:problem:{code.Replace('_', '-')}";
}
