using System.Globalization;
using System.Text.Json;

namespace MyTranslator.Api.AiConfiguration;

public static class AiConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapAiConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/providers",
                async Task<IResult> (AiConfigurationService service, CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListProvidersAsync(cancellationToken)))
            .WithName("ListAiProviders")
            .WithTags("AI Configuration");

        endpoints.MapPost(
                "/api/providers",
                async Task<IResult> (
                    JsonElement body,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var provider = await service.CreateProviderAsync(ParseProvider(body), cancellationToken);
                        return Results.Created($"/api/providers/{provider.Id}", provider);
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("CreateAiProvider")
            .WithTags("AI Configuration");

        endpoints.MapPut(
                "/api/providers/{id:guid}",
                async Task<IResult> (
                    Guid id,
                    JsonElement body,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        return Results.Ok(await service.UpdateProviderAsync(id, ParseProvider(body), cancellationToken));
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("UpdateAiProvider")
            .WithTags("AI Configuration");

        endpoints.MapDelete(
                "/api/providers/{id:guid}",
                async Task<IResult> (
                    Guid id,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await service.DeleteProviderAsync(id, cancellationToken);
                        return Results.NoContent();
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("DeleteAiProvider")
            .WithTags("AI Configuration");

        endpoints.MapGet(
                "/api/providers/{providerId:guid}/models",
                async Task<IResult> (
                    Guid providerId,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        return Results.Ok(await service.ListModelsAsync(providerId, cancellationToken));
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("ListAiModels")
            .WithTags("AI Configuration");

        endpoints.MapPost(
                "/api/providers/{providerId:guid}/models",
                async Task<IResult> (
                    Guid providerId,
                    JsonElement body,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var model = await service.CreateModelAsync(providerId, ParseModel(body), cancellationToken);
                        return Results.Created($"/api/providers/{providerId}/models/{model.Id}", model);
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("CreateAiModel")
            .WithTags("AI Configuration");

        endpoints.MapPut(
                "/api/providers/{providerId:guid}/models/{id:guid}",
                async Task<IResult> (
                    Guid providerId,
                    Guid id,
                    JsonElement body,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        return Results.Ok(await service.UpdateModelAsync(
                            providerId,
                            id,
                            ParseModel(body),
                            cancellationToken));
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("UpdateAiModel")
            .WithTags("AI Configuration");

        endpoints.MapDelete(
                "/api/providers/{providerId:guid}/models/{id:guid}",
                async Task<IResult> (
                    Guid providerId,
                    Guid id,
                    AiConfigurationService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await service.DeleteModelAsync(providerId, id, cancellationToken);
                        return Results.NoContent();
                    }
                    catch (AiConfigurationRequestException exception)
                    {
                        return Problem(exception);
                    }
                })
            .WithName("DeleteAiModel")
            .WithTags("AI Configuration");

        return endpoints;
    }

    private static ProviderWriteRequest ParseProvider(JsonElement body)
    {
        RequireObject(body);
        if (body.TryGetProperty("apiKeyMasked", out _))
        {
            throw Invalid(
                "invalid_provider_key_field",
                "apiKeyMasked is a response-only field.");
        }

        var name = RequiredNonBlankString(body, "name", "invalid_provider_name").Trim();
        var kind = RequiredNonBlankString(body, "kind", "invalid_provider_kind").Trim().ToLowerInvariant();
        if (kind is not ("openai" or "ollama"))
        {
            throw Invalid("invalid_provider_kind", "The provider kind must be openai or ollama.");
        }

        var baseUrl = OptionalString(body, "baseUrl")?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = null;
        }
        else if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw Invalid("invalid_base_url", "baseUrl must be an absolute HTTP or HTTPS URL.");
        }

        var apiKey = OptionalString(body, "apiKey");
        var enabled = OptionalBoolean(body, "enabled", true);
        var isDefault = OptionalBoolean(body, "isDefault", false);
        var batchSize = OptionalInteger(body, "batchSize", 20);
        var maxAttempts = OptionalInteger(body, "maxAttempts", 3);
        var requestTimeout = OptionalTimeSpan(body, "requestTimeout", TimeSpan.FromMinutes(1));
        if (batchSize is < 1 or > 200 || maxAttempts is < 1 or > 10 || requestTimeout <= TimeSpan.Zero)
        {
            throw new AiConfigurationRequestException(
                "invalid_runtime_settings",
                "Provider runtime settings are outside the supported range.",
                StatusCodes.Status400BadRequest,
                new Dictionary<string, object?>
                {
                    ["batchSize"] = "1..200",
                    ["requestTimeout"] = "> 00:00:00",
                    ["maxAttempts"] = "1..10"
                });
        }

        return new ProviderWriteRequest(
            name,
            kind,
            baseUrl,
            apiKey,
            enabled,
            isDefault,
            batchSize,
            requestTimeout,
            maxAttempts);
    }

    private static ModelWriteRequest ParseModel(JsonElement body)
    {
        RequireObject(body);
        var modelId = RequiredNonBlankString(body, "modelId", "invalid_model").Trim();
        var displayName = RequiredNonBlankString(body, "displayName", "invalid_model").Trim();
        return new ModelWriteRequest(
            modelId,
            displayName,
            OptionalBoolean(body, "supportsThinking", false),
            OptionalBoolean(body, "supportsToolUse", false),
            OptionalBoolean(body, "supportsStreaming", false),
            OptionalBoolean(body, "isDefault", false));
    }

    private static void RequireObject(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("invalid_request", "The request body must be a JSON object.");
        }
    }

    private static string RequiredNonBlankString(JsonElement body, string property, string code)
    {
        if (!body.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(code, $"{property} is required.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid("invalid_request", $"{property} must be a string or null.");
        }

        return value.GetString();
    }

    private static bool OptionalBoolean(JsonElement body, string property, bool defaultValue)
    {
        if (!body.TryGetProperty(property, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("invalid_request", $"{property} must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int OptionalInteger(JsonElement body, string property, int defaultValue)
    {
        if (!body.TryGetProperty(property, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Invalid("invalid_runtime_settings", $"{property} must be an integer.");
        }

        return result;
    }

    private static TimeSpan OptionalTimeSpan(JsonElement body, string property, TimeSpan defaultValue)
    {
        if (!body.TryGetProperty(property, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !TimeSpan.TryParseExact(value.GetString(), "c", CultureInfo.InvariantCulture, out var result))
        {
            throw Invalid("invalid_runtime_settings", $"{property} must use the TimeSpan constant format.");
        }

        return result;
    }

    private static AiConfigurationRequestException Invalid(string code, string message) => new(
        code,
        message,
        StatusCodes.Status400BadRequest);

    private static IResult Problem(AiConfigurationRequestException exception) => ApiProblem.Create(
        exception.Code,
        exception.Message,
        exception.StatusCode,
        exception.Errors);
}
