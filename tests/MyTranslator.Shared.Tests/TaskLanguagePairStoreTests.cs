using Microsoft.JSInterop;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 历史对比语言对持久化回归（与翻译面板选择持久化同一前端约定：刷新后恢复上次输入）。
/// </summary>
public class TaskLanguagePairStoreTests
{
    private static readonly Guid TaskId = Guid.Parse("4d898d78-1f24-47e9-8e30-adcee716c13d");

    private sealed class RecordingJs : IJSRuntime
    {
        public readonly Dictionary<string, string> Store = [];
        public string? RemovedKey;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
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
            InvokeAsync<object>(identifier, args);
            return ValueTask.CompletedTask;
        }

        public ValueTask InvokeAsync(string identifier, CancellationToken cancellationToken, params object?[]? args)
            => InvokeAsync(identifier, args);
    }

    [Fact]
    public async Task Save后Load_往返一致且规范化()
    {
        var js = new RecordingJs();
        await TaskLanguagePairStore.SaveAsync(js, TaskId, new LanguagePair(" en ", "zh-CN"));

        var pair = await TaskLanguagePairStore.LoadAsync(js, TaskId);

        Assert.Equal("en", pair.SourceLanguage);
        Assert.Equal("zh-CN", pair.TargetLanguage);
    }

    [Fact]
    public async Task 无记录_返回双null语言对()
    {
        var pair = await TaskLanguagePairStore.LoadAsync(new RecordingJs(), TaskId);

        Assert.Null(pair.SourceLanguage);
        Assert.Null(pair.TargetLanguage);
        Assert.False(pair.IsComplete);
    }

    [Fact]
    public async Task 双空保存_清除记录()
    {
        var js = new RecordingJs();
        await TaskLanguagePairStore.SaveAsync(js, TaskId, new LanguagePair("en", "zh-CN"));
        await TaskLanguagePairStore.SaveAsync(js, TaskId, new LanguagePair(null, null));

        Assert.Equal($"mt.languagePair.{TaskId:N}", js.RemovedKey);
        var pair = await TaskLanguagePairStore.LoadAsync(js, TaskId);
        Assert.Null(pair.SourceLanguage);
        Assert.Null(pair.TargetLanguage);
    }

    [Fact]
    public async Task 损坏记录_按未设置处理()
    {
        var js = new RecordingJs();
        js.Store[$"mt.languagePair.{TaskId:N}"] = "{not json";

        var pair = await TaskLanguagePairStore.LoadAsync(js, TaskId);

        Assert.Null(pair.SourceLanguage);
        Assert.Null(pair.TargetLanguage);
    }
}
