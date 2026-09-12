using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Rules;

public sealed class RuleCheckOptions
{
    public string[] DisabledRules { get; set; } = [];
}

/// <summary>配置只影响诊断查询，不关闭翻译、保存和导出的结构保护。</summary>
public sealed class RuleEngine(IEnumerable<ITranslationRule> rules, IOptions<RuleCheckOptions> options)
{
    public IReadOnlyList<string> EnabledRules => rules.Where(IsEnabled).Select(rule => rule.Id).ToArray();

    public IReadOnlyList<RuleFinding> Evaluate(TranslationRuleContext context) => rules
        .Where(IsEnabled)
        .Select(rule => (rule.Id, Violation: rule.Evaluate(context)))
        .Where(item => item.Violation is not null)
        .Select(item => new RuleFinding(item.Id, item.Violation!.Code, "targetText", item.Violation.Offset, item.Violation.Length))
        .ToArray();

    private bool IsEnabled(ITranslationRule rule) => !options.Value.DisabledRules.Contains(rule.Id, StringComparer.Ordinal);
}

public sealed record RuleFinding(string RuleId, string Code, string Field, int Offset, int Length);
public sealed record RuleCheckRequest(int ExtractionRevision);
public sealed record SegmentRuleFindings(Guid SegmentId, int Order, int Version, IReadOnlyList<RuleFinding> Violations);
public sealed record RuleCheckResponse(Guid TaskId, int ExtractionRevision, int TotalSegments,
    int ViolatingSegments, IReadOnlyList<string> EnabledRules, IReadOnlyList<SegmentRuleFindings> Items);

public sealed class RuleCheckService(AppDbContext database, RuleEngine engine)
{
    public async Task<RuleCheckResponse> CheckAsync(Guid taskId, RuleCheckRequest request, CancellationToken cancellationToken)
    {
        if (request.ExtractionRevision < 1)
            throw new RuleRequestException("invalid_extraction_revision", "A positive extraction revision is required.", 400);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await database.TranslationTasks.AsNoTracking().SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken)
            ?? throw new RuleRequestException("task_not_found", "Task not found.", 404);
        if (task.ExtractionRevision != request.ExtractionRevision)
            throw new RuleRequestException("extraction_revision_changed", "The extraction revision has changed.", 409);
        var segments = await database.TranslationSegments.AsNoTracking().Where(segment => segment.TaskId == taskId)
            .OrderBy(segment => segment.Order).ToListAsync(cancellationToken);
        var items = segments.Select(segment => new SegmentRuleFindings(segment.Id, segment.Order, segment.Version,
                engine.Evaluate(new(segment.Id, segment.SourceText, segment.TargetText, segment.MarkupTableJson))))
            .Where(item => item.Violations.Count > 0).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new(taskId, task.ExtractionRevision, segments.Count, items.Length, engine.EnabledRules, items);
    }
}

public sealed class RuleRequestException(string code, string message, int statusCode)
    : ApiRequestException(code, message, statusCode);

public static class RuleEndpoints
{
    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/tasks/{taskId:guid}/rule-checks",
                async (Guid taskId, RuleCheckRequest request, RuleCheckService service, CancellationToken cancellationToken) =>
                    Results.Ok(await service.CheckAsync(taskId, request, cancellationToken)))
            .WithName("CheckTaskRules").WithTags("Rules");
        return endpoints;
    }
}
