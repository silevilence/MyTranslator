using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.Terms;

namespace MyTranslator.Api.TaskOperations;

public sealed record TaskReferenceResponse(Guid? GlossaryId, string? BackgroundInformation, int Version);

public sealed class TaskReferenceService(AppDbContext database, TaskOperationLock operationLock)
{
    public async Task<TaskReferenceResponse> GetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await database.TranslationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw Problem("task_not_found", 404);
        return ToResponse(task);
    }

    public async Task<TaskReferenceResponse> UpdateAsync(Guid taskId, TaskReferenceResponse request, CancellationToken cancellationToken)
    {
        if (request.Version < 1) throw Problem("invalid_task_reference_version", 400);
        var background = string.IsNullOrWhiteSpace(request.BackgroundInformation) ? null : request.BackgroundInformation.Trim();
        if (background?.Length > 4000) throw Problem("invalid_background_information", 400);
        await using var operation = operationLock.TryAcquire(taskId) ?? throw Problem("task_busy", 409);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var task = await database.TranslationTasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw Problem("task_not_found", 404);
        if (task.Status == TranslationTaskStatus.Processing ||
            await database.TranslationRuns.AnyAsync(r => r.ActiveTaskLockId == taskId, cancellationToken) ||
            await database.ReviewRuns.AnyAsync(r => r.ActiveTaskLockId == taskId, cancellationToken))
            throw Problem("task_busy", 409);
        if (task.ReferenceVersion != request.Version) throw Problem("task_reference_version_conflict", 409);
        if (request.GlossaryId is { } glossaryId) await GlossaryService.RequireAsync(database, glossaryId, cancellationToken);
        task.GlossaryId = request.GlossaryId;
        task.BackgroundInformation = background;
        task.ReferenceVersion++;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw Problem("task_reference_version_conflict", 409); }
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(task);
    }

    private static TaskReferenceResponse ToResponse(TranslationTask task) => new(task.GlossaryId, task.BackgroundInformation, task.ReferenceVersion);
    private static TermRequestException Problem(string code, int status) => new(code, "Task reference request failed.", status);
}
