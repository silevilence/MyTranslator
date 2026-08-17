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
                        return ApiProblem.Create(
                            exception.Code,
                            exception.Message,
                            exception.StatusCode);
                    }
                })
            .WithName("ListTasks")
            .WithTags("Tasks");

        return endpoints;
    }
}
