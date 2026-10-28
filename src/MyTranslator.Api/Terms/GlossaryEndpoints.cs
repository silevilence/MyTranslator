using System.Text.Json;
using MyTranslator.Api.TaskOperations;

namespace MyTranslator.Api.Terms;

public static class GlossaryEndpoints
{
    public static IEndpointRouteBuilder MapGlossaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/glossaries", async (int? limit, string? cursor, GlossaryService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(limit ?? 100, cursor, ct)));
        endpoints.MapPost("/api/glossaries", async (JsonElement body, GlossaryService service, CancellationToken ct) =>
        {
            var glossary = await service.CreateAsync(ReadString(body, "name", "invalid_glossary_name"), ct);
            return Results.Created($"/api/glossaries/{glossary.Id}", glossary);
        });
        endpoints.MapPut("/api/glossaries/{id:guid}", async (Guid id, JsonElement body, GlossaryService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, ReadString(body, "name", "invalid_glossary_name"),
                ReadVersion(body, "invalid_glossary_version"), ct)));
        endpoints.MapDelete("/api/glossaries/{id:guid}", async (Guid id, HttpRequest request, GlossaryService service, CancellationToken ct) =>
        {
            if (!int.TryParse(request.Query["version"], out var version)) throw Invalid("invalid_glossary_version");
            await service.DeleteAsync(id, version, ct);
            return Results.NoContent();
        });
        endpoints.MapGet("/api/tasks/{id:guid}/reference", async (Guid id, TaskReferenceService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, ct)));
        endpoints.MapPut("/api/tasks/{id:guid}/reference", async (Guid id, JsonElement body, TaskReferenceService service, CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("glossaryId", out _)) throw Invalid("invalid_glossary_id");
            var request = new TaskReferenceResponse(ReadId(body), ReadString(body, "backgroundInformation", "invalid_background_information"),
                ReadVersion(body, "invalid_task_reference_version"));
            return Results.Ok(await service.UpdateAsync(id, request, ct));
        });
        return endpoints;
    }

    internal static Guid? ReadId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw Invalid("invalid_glossary_id");
        if (!body.TryGetProperty("glossaryId", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty
            ? id : throw Invalid("invalid_glossary_id");
    }

    private static string? ReadString(JsonElement body, string field, string code)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(field, out var value)) throw Invalid(code);
        return value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), _ => throw Invalid(code) };
    }
    private static int ReadVersion(JsonElement body, string code) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("version", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var version) && version > 0 ? version : throw Invalid(code);
    private static TermRequestException Invalid(string code) => new(code, "Invalid reference request.", 400);
}
