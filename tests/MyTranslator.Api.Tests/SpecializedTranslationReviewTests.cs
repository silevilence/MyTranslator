using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;
using MyTranslator.Api.Review;
using MyTranslator.Api.TaskOperations;
using static MyTranslator.Api.Tests.SpecializedTranslationExecutionTests;

namespace MyTranslator.Api.Tests;

public sealed class SpecializedTranslationReviewTests
{
    [Fact]
    public async Task ReviewStopsRetryWhenModelBecomesSpecializedDuringResponse()
    {
        Func<Task>? changeFormat = null;
        var handler = new CaptureHandler("openai", (_, _) => "invalid review response")
        {
            BeforeResponse = _ => changeFormat!()
        };
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, "general");
        var taskId = await SpecializedTestApi.Import(client, "Hello");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await database.TranslationSegments.SingleAsync()).TargetText = "你好";
        await database.SaveChangesAsync();
        changeFormat = async () =>
        {
            await using var updateScope = factory.Services.CreateAsyncScope();
            var updateDatabase = updateScope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await updateDatabase.Models.SingleAsync()).TranslationMessageFormat = "hy-mt2";
            await updateDatabase.SaveChangesAsync();
        };
        var service = new ReviewRunService(database, new ReviewRunQueue(), scope.ServiceProvider.GetRequiredService<TaskOperationLock>());
        var run = await service.CreateAsync(taskId, new CreateReviewRunRequest(1, "en", "zh-CN", providerId, modelId), default);
        await scope.ServiceProvider.GetRequiredService<ReviewRunProcessor>().ProcessAsync(run.RunId, default);
        database.ChangeTracker.Clear();
        var failure = await database.ReviewRunFailures.SingleAsync();
        Assert.Equal("model_not_review_capable", failure.Code);
        Assert.Equal(1, failure.Attempts);
        Assert.Single(handler.Requests);
        Assert.Equal("你好", (await database.TranslationSegments.SingleAsync()).TargetText);
    }

    [Theory]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task ReviewRejectsSpecializedModelsForEverySelectionShape(string format)
    {
        var handler = new CaptureHandler("openai", (_, _) => "你好");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, format);
        var taskId = await SpecializedTestApi.Import(client, "Hello");
        await Run(client, taskId);
        foreach (var choice in new[] { 0, 1, 2 })
        {
            var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/review-runs", new
            {
                extractionRevision = 1, targetLanguage = "zh-CN",
                providerId = choice > 0 ? (Guid?)providerId : null, modelId = choice > 1 ? (Guid?)modelId : null
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("model_not_review_capable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Single(handler.Requests);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await database.ReviewRuns.ToListAsync());
        Assert.Equal("你好", (await database.TranslationSegments.SingleAsync()).TargetText);
        Assert.Equal(TranslationTaskStatus.Completed, (await database.TranslationTasks.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReviewRechecksFormatBetweenCreationAndExecution()
    {
        var handler = new CaptureHandler("openai", (_, _) => "你好");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, "hy-mt2");
        var taskId = await SpecializedTestApi.Import(client, "Hello");
        await Run(client, taskId);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = await database.Models.SingleAsync();
        model.TranslationMessageFormat = "general";
        await database.SaveChangesAsync();
        // 独立队列使配置变化发生在创建与实际执行之间，不依赖后台线程时序。
        var service = new ReviewRunService(database, new ReviewRunQueue(), scope.ServiceProvider.GetRequiredService<TaskOperationLock>());
        var run = await service.CreateAsync(taskId, new CreateReviewRunRequest(1, "en", "zh-CN", providerId, modelId), default);
        model.TranslationMessageFormat = "bilibili-index-translate";
        await database.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ReviewRunProcessor>().ProcessAsync(run.RunId, default);
        database.ChangeTracker.Clear();
        Assert.Equal(ReviewRunStatus.Failed, (await database.ReviewRuns.SingleAsync()).Status);
        var failure = await database.ReviewRunFailures.SingleAsync();
        Assert.Equal("model_not_review_capable", failure.Code);
        Assert.Equal(0, failure.Attempts);
        Assert.Single(handler.Requests);
        Assert.Equal("你好", (await database.TranslationSegments.SingleAsync()).TargetText);
    }
}
