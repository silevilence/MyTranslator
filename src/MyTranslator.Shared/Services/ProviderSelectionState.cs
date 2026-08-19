using Microsoft.JSInterop;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 任务级提供商/模型选择的共享状态：内存缓存 + localStorage 持久化（经 <see cref="ProviderSelectionStore"/>）+ 变更通知。
/// 同一任务页的翻译与审核面板复用同一选择（AI 翻译约定 §11.1 第 6 步 / AI 审核约定 §12.1 第 1 步：
/// 缺省显示默认对，选择按任务维度持久化，刷新后恢复），一处修改、处处可见。
/// </summary>
public sealed class ProviderSelectionState : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Guid _taskId;
    private Guid? _providerId;
    private Guid? _modelId;
    private bool _loaded;

    /// <summary>选择变化通知（持久化完成后触发）；订阅方自行校验选择对当前配置仍有效。</summary>
    public event Func<Task>? Changed;

    public ProviderSelectionState(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// 读取任务的当前选择：首个读取方从 localStorage 加载并缓存，后续读取直接命中缓存。
    /// 无记录或记录损坏返回双 null（表示走默认对）。
    /// </summary>
    public async Task<(Guid? ProviderId, Guid? ModelId)> GetAsync(Guid taskId)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_loaded || _taskId != taskId)
            {
                (_providerId, _modelId) = await ProviderSelectionStore.LoadAsync(_js, taskId);
                _taskId = taskId;
                _loaded = true;
            }

            return (_providerId, _modelId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>更新并持久化任务的选择；双 null 表示清除记录（回默认对）。随后触发 <see cref="Changed"/>。</summary>
    public async Task SetAsync(Guid taskId, Guid? providerId, Guid? modelId)
    {
        await _gate.WaitAsync();
        try
        {
            await ProviderSelectionStore.SaveAsync(_js, taskId, providerId, modelId);
            _taskId = taskId;
            _providerId = providerId;
            _modelId = modelId;
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }

        if (Changed is { } handlers)
        {
            // 多播委托只返回最后一个返回值，须逐个等待全部处理器
            foreach (Func<Task> handler in handlers.GetInvocationList().Cast<Func<Task>>())
            {
                await handler();
            }
        }
    }


    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
