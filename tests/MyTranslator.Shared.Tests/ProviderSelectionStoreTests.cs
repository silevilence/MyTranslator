using Microsoft.JSInterop;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 翻译面板选择持久化回归（AI 翻译接口约定 §11.1 第 6 步：刷新后选择一致）。
/// </summary>
public class ProviderSelectionStoreTests
{
    private static readonly Guid TaskId = Guid.Parse("4d898d78-1f24-47e9-8e30-adcee716c13d");
    private static readonly Guid ProviderId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid ModelId = Guid.Parse("f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b");

    private sealed class RecordingJs : IJSRuntime
    {
        public readonly Dictionary<string, string> Store = [];
        public string? RemovedKey;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            // JSRuntimeExtensions.InvokeVoidAsync 会经泛型重载转发 setItem/removeItem
            if (identifier == "mtStorage.setItem")
            {
                Store[(string)args![0]!] = (string)args[1]!;
                return ValueTask.FromResult(default(TValue)!);
            }

            if (identifier == "mtStorage.removeItem")
            {
                RemovedKey = (string)args![0]!;
                Store.Remove(RemovedKey);
                return ValueTask.FromResult(default(TValue)!);
            }

            Assert.Equal("mtStorage.getItem", identifier);
            var key = Assert.IsType<string>(args![0]);
            return ValueTask.FromResult(Store.TryGetValue(key, out var value)
                ? (TValue)(object)value
                : (TValue)(object?)null!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, params object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        public ValueTask InvokeAsync(string identifier, object?[]? args)
        {
            if (identifier == "mtStorage.setItem")
            {
                Store[(string)args![0]!] = (string)args[1]!;
            }
            else if (identifier == "mtStorage.removeItem")
            {
                RemovedKey = (string)args![0]!;
                Store.Remove(RemovedKey);
            }
            else
            {
                Assert.Fail($"未预期的调用：{identifier}");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask InvokeAsync(string identifier, CancellationToken cancellationToken, params object?[]? args)
        {
            if (identifier == "mtStorage.setItem")
            {
                Store[(string)args![0]!] = (string)args[1]!;
            }
            else if (identifier == "mtStorage.removeItem")
            {
                RemovedKey = (string)args![0]!;
                Store.Remove(RemovedKey);
            }
            else
            {
                Assert.Fail($"未预期的调用：{identifier}");
            }

            return ValueTask.CompletedTask;
        }
    }


    [Fact]
    public async Task Save后Load_往返一致()
    {
        var js = new RecordingJs();

        await ProviderSelectionStore.SaveAsync(js, TaskId, ProviderId, ModelId);
        var (providerId, modelId) = await ProviderSelectionStore.LoadAsync(js, TaskId);

        Assert.Equal(ProviderId, providerId);
        Assert.Equal(ModelId, modelId);
        Assert.Null(js.RemovedKey);
    }

    [Fact]
    public async Task 只选提供商时_模型为null()
    {
        var js = new RecordingJs();

        await ProviderSelectionStore.SaveAsync(js, TaskId, ProviderId, null);
        var (providerId, modelId) = await ProviderSelectionStore.LoadAsync(js, TaskId);

        Assert.Equal(ProviderId, providerId);
        Assert.Null(modelId);
    }

    [Fact]
    public async Task 双null保存_清除记录_读取回默认对()
    {
        var js = new RecordingJs();
        await ProviderSelectionStore.SaveAsync(js, TaskId, ProviderId, ModelId);

        await ProviderSelectionStore.SaveAsync(js, TaskId, null, null);
        var (providerId, modelId) = await ProviderSelectionStore.LoadAsync(js, TaskId);

        Assert.Null(providerId);
        Assert.Null(modelId);
        Assert.NotNull(js.RemovedKey);
    }

    [Fact]
    public async Task 无记录_返回双null()
    {
        var js = new RecordingJs();

        var (providerId, modelId) = await ProviderSelectionStore.LoadAsync(js, TaskId);

        Assert.Null(providerId);
        Assert.Null(modelId);
    }

    [Fact]
    public async Task 损坏记录_按未选择处理()
    {
        var js = new RecordingJs();
        js.Store[$"mt.providerSelection.{TaskId:N}"] = "{{{ not json";

        var (providerId, modelId) = await ProviderSelectionStore.LoadAsync(js, TaskId);

        Assert.Null(providerId);
        Assert.Null(modelId);
    }
}
