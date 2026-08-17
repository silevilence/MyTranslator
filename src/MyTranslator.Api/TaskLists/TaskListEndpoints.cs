namespace MyTranslator.Api.TaskLists;

public static class TaskListEndpoints
{
    public static IEndpointRouteBuilder MapTaskListEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/tasks",
                async Task<IResult> (
                    string? status,
                    int? limit,
                    string? cursor,
                    TaskListService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        return Results.Ok(await service.ListAsync(
                            status,
                            limit ?? 100,
                            cursor,
                            cancellationToken));
                    }
                    catch (TaskListRequestException exception)
                    {
                        return Results.Problem(
                            statusCode: exception.StatusCode,
                            title: exception.Message,
                            type: $"urn:mytranslator:problem:{exception.Code.Replace('_', '-')}",
                            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
                    }
                })
            .WithName("ListTasks")
            .WithTags("Tasks");

        return endpoints;
    }
}
