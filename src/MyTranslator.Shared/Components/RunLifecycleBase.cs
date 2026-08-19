using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Resources;
using MyTranslator.Shared.Services;

namespace MyTranslator.Shared.Components;

/// <summary>分段级失败行的展示形状（基类 → 子类渲染共用；与资源类型解耦）。</summary>
public sealed record RunFailureItem(int Order, string? Code, int Attempts);

/// 运行面板生命周期基座（AI 翻译 / AI 审核面板共用）：
/// 运行状态、Retry-After 轮询（含创建响应首间隔）、瞬态退避、活动状态通知与分段级失败加载。
/// 子类只提供运行形状适配与各面板差异化的创建错误分支/事件语义，
/// 轮询细节单源维护，杜绝两份 ~600 行同构脚手架漂移（本次审核 Sp3 即漂移实例）。
/// </summary>
/// <typeparam name="TRun">运行资源形状（TranslationRun / ReviewRun）。</typeparam>
public abstract class RunLifecycleBase<TRun> : ComponentBase, IDisposable
    where TRun : class
{
    /// <summary>轮询连续瞬态失败次数上限：达到后停止轮询并展示错误横幅。</summary>
    protected const int MaxPollRetries = 5;

    /// <summary>轮询瞬态失败指数退避上限（毫秒）。</summary>
    protected const int MaxPollBackoffMs = 30000;

    /// <summary>失败明细读取上限（终态后契约要求读完整列表；防御极端规模保护渲染）。</summary>
    protected const int MaxFailureItems = 5000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected TRun? Run;
    protected bool FailuresTruncated;
    protected string? PollError;
    protected int LastSucceeded;
    protected int PollIntervalMs = 1000;
    private CancellationTokenSource? _pollCts;
    private bool _lastNotifiedActivity;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    protected ApiErrorMessageProvider ErrorProvider { get; set; } = default!;

    [Inject]
    protected IStringLocalizer<RunPanelStrings> RunStrings { get; set; } = default!;

    /// <summary>任务 ID。</summary>
    [Parameter, EditorRequired]
    public required Guid TaskId { get; set; }

    /// <summary>
    /// 任务存在他方活动互斥操作（同锁互斥）：创建会被 409 task_busy 拒绝，提前禁用本面板；
    /// 终态由调用方清除。
    /// </summary>
    [Parameter]
    public bool ExternalBusy { get; set; }

    /// <summary>本地运行活动状态翻转（进入/离开活动态）时触发；调用方据此互斥禁用另一运行面板。</summary>
    [Parameter]
    public EventCallback<bool> OnActivityChanged { get; set; }

    /// <summary>存在未进入终态的本地运行；活动期间禁止再次创建（互斥）。</summary>
    protected bool HasActiveRun => Run is not null && !IsTerminal(Run);

    /// <summary>运行失效错误码（如 translation_run_not_found / review_run_not_found）。</summary>
    protected abstract string RunNotFoundCode { get; }

    /// <summary>读取运行详情并附带 Retry-After（§5）。</summary>
    protected abstract Task<(TRun Run, int? RetryAfterSeconds)> GetRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>分页读取分段级失败（终态后调用）。</summary>
    protected abstract Task<(IReadOnlyList<RunFailureItem> Items, string? NextCursor)> GetFailuresAsync(Guid runId, int limit, string? cursor);

    /// <summary>运行成功分段数（用于检测「逐批提交」增量以刷新分段）。</summary>
    protected abstract int? SucceededOf(TRun run);

    /// <summary>成功分段数增加时触发（子类转发到各自的事件语义）。</summary>
    protected abstract Task OnSucceededIncreasedAsync();

    /// <summary>运行进入终态时触发（子类转发到各自的事件语义）。</summary>
    protected abstract Task OnTerminalAsync();

    /// <summary>运行失效或状态可能变化时触发（子类转发到各自的事件语义）。</summary>
    protected abstract Task OnStateChangedAsync();

    /// <summary>运行是否进入终态：以服务端 finishedAt 为准，未知枚举值带 finishedAt 视为终态（§2.1）。</summary>
    protected abstract bool IsTerminal(TRun run);

    /// <summary>活动状态翻转时通知调用方（去重：仅在实际变化时触发）。</summary>
    protected async Task NotifyActivityChangedAsync()
    {
        var active = HasActiveRun;
        if (active == _lastNotifiedActivity)
        {
            return;
        }

        _lastNotifiedActivity = active;
        await OnActivityChanged.InvokeAsync(active);
    }

    /// <summary>
    /// 创建成功后应用创建响应 Retry-After 作为首 polling 间隔（§5 / 本次审核 Sp3 修复），
    /// 重置失败列表与成功计数并启动轮询。
    /// </summary>
    protected async Task OnCreateSucceededAsync(TRun run, int? retryAfterSeconds)
    {
        Run = run;
        FailuresTruncated = false;
        LastSucceeded = 0;
        PollIntervalMs = Math.Max(retryAfterSeconds ?? 1, 1) * 1000;
        await SetFailuresAsync([], notify: false);
        StartPolling();
        await NotifyActivityChangedAsync();
    }

    /// <summary>读取最新运行列表第一项恢复状态（页面刷新后 §11.1 第 6 步 / §12.1 第 6 步）；非终态恢复轮询。</summary>
    protected async Task RestoreLatestRunAsync(TRun? run)
    {
        try
        {
            if (run is not null)
            {
                Run = run;
                LastSucceeded = SucceededOf(run) ?? 0;
                if (IsTerminal(run))
                {
                    if (IsFailureTerminal(run))
                    {
                        await LoadFailuresAsync();
                    }
                }
                else
                {
                    StartPolling();
                }
            }
        }
        catch (Exception ex) when (ex is ApiErrorException or HttpRequestException)
        {
            PollError = ErrorProvider.ForException(ex);
        }

        StateHasChanged();
        await NotifyActivityChangedAsync();
    }

    /// <summary>终态为部分失败/失败时才需要读失败列表。</summary>
    protected abstract bool IsFailureTerminal(TRun run);

    /// <summary>启动轮询循环（幂等：取消旧循环后重启）。</summary>
    protected void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    /// <summary>
    /// 按 Retry-After 轮询运行详情（§5）：间隔不得短于 1 秒；进度与阶段文案一律取服务端快照。
    /// 运行失效（404/409）立即停止并通知页面；瞬态错误（5xx/网络）指数退避续轮，
    /// 连续失败达到上限后停止并展示错误横幅；其余契约错误（4xx）立即停止，不做退避重试。
    /// </summary>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var pollErrors = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && Run is not null && !IsTerminal(Run))
            {
                await Task.Delay(PollIntervalMs, cancellationToken);
                try
                {
                    var (run, retryAfter) = await GetRunAsync(RunIdOf(Run), cancellationToken);
                    pollErrors = 0;
                    PollIntervalMs = Math.Max(retryAfter ?? 1, 1) * 1000;
                    PollError = null;
                    Run = run;

                    var succeededNow = SucceededOf(run);
                    if (succeededNow is > 0 && succeededNow.Value > LastSucceeded)
                    {
                        LastSucceeded = succeededNow.Value;
                        await OnSucceededIncreasedAsync();
                    }

                    StateHasChanged();

                    if (IsTerminal(Run))
                    {
                        await HandleTerminalAsync();
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ApiErrorException ex) when (IsRunInvalidated(ex.Code))
                {
                    await HandleRunInvalidatedAsync(ex);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or ApiErrorException { StatusCode: >= 500 })
                {
                    // 瞬态错误（5xx/网络）：指数退避后重试
                    pollErrors++;
                    if (pollErrors >= MaxPollRetries)
                    {
                        PollIntervalMs = 1000;
                        PollError = ErrorProvider.ForException(ex);
                        StateHasChanged();
                        return;
                    }

                    PollIntervalMs = Math.Min(PollIntervalMs * 2, MaxPollBackoffMs);
                }
                catch (ApiErrorException ex)
                {
                    // 其余契约错误（4xx）：非瞬态，立即停止并展示错误横幅
                    PollIntervalMs = 1000;
                    PollError = ErrorProvider.ForError(ex);
                    StateHasChanged();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 组件销毁或重启轮询：静默退出
        }
    }

    /// <summary>终态统一处理：读失败列表（如失败终态）→ 子类终态事件 → 活动状态通知。</summary>
    private async Task HandleTerminalAsync()
    {
        if (Run is not null && IsFailureTerminal(Run))
        {
            await LoadFailuresAsync();
        }

        await OnTerminalAsync();
        await NotifyActivityChangedAsync();
    }

    /// <summary>运行随重新提取失效（§2.4）：重置本地状态并通知页面按新修订重建。</summary>
    private async Task HandleRunInvalidatedAsync(ApiErrorException ex)
    {
        ResetRunState();
        Snackbar.Add(ErrorProvider.ForError(ex), Severity.Warning);
        await OnStateChangedAsync();
        await NotifyActivityChangedAsync();
    }

    /// <summary>
    /// 沿游标读取分段级失败完整列表（超 <see cref="MaxFailureItems"/> 截断并提示）。
    /// 与轮询循环串行化，避免终态读取与失效重置交错写状态。
    /// </summary>
    protected async Task LoadFailuresAsync()
    {
        if (Run is null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            var items = new List<RunFailureItem>();
            string? cursor = null;
            do
            {
                var (pageItems, nextCursor) = await GetFailuresAsync(RunIdOf(Run), 200, cursor);
                items.AddRange(pageItems);
                cursor = nextCursor;
            }
            while (cursor is not null && items.Count < MaxFailureItems);

            FailuresTruncated = cursor is not null;
            await SetFailuresAsync(items, notify: true);
        }
        catch (ApiErrorException ex) when (IsRunInvalidated(ex.Code))
        {
            await HandleRunInvalidatedAsync(ex);
        }
        catch (Exception ex) when (ex is ApiErrorException or HttpRequestException)
        {
            PollError = ErrorProvider.ForException(ex);
        }
        finally
        {
            _gate.Release();
        }

        StateHasChanged();
    }

    /// <summary>子类实现：把失败列表写入自己的渲染状态（notify=true 时触发重渲染）。</summary>
    protected abstract Task SetFailuresAsync(IReadOnlyList<RunFailureItem> items, bool notify);

    /// <summary>重置本地运行状态（运行失效时使用）。</summary>
    protected void ResetRunState()
    {
        Run = null;
        FailuresTruncated = false;
    }

    /// <summary>运行失效判定（§2.4）。</summary>
    protected bool IsRunInvalidated(string? code) => code is not null && code == RunNotFoundCode || code == "extraction_revision_changed";

    /// <summary>提取运行 ID。</summary>
    protected abstract Guid RunIdOf(TRun run);

    /// <summary>共享状态徽标：文字 + 语义色 + 图标三通道（RunPanelStrings 单源）。</summary>
    protected (string Text, Color Color, string Icon) StatusVisual(TRun run)
    {
        var (key, color, icon) = RunStatusVisual.Resolve(StatusOf(run), FinishedOf(run));
        return (RunStrings[key].Value, color, icon);
    }

    /// <summary>提取运行状态字符串。</summary>
    protected abstract string? StatusOf(TRun run);

    /// <summary>提取 finishedAt 是否非空。</summary>
    protected abstract bool FinishedOf(TRun run);

    /// <summary>错误横幅重试：无运行恢复列表、终态重读失败、活动恢复轮询。</summary>
    protected async Task RetryPollAsync()
    {
        PollError = null;
        if (Run is null)
        {
            await OnRetryWithoutRunAsync();
        }
        else if (IsTerminal(Run))
        {
            await LoadFailuresAsync();
        }
        else
        {
            StartPolling();
        }
    }

    /// <summary>无本地运行时的错误重试（子类恢复最新运行）。</summary>
    protected abstract Task OnRetryWithoutRunAsync();

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _gate.Dispose();
    }
}
