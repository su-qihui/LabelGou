namespace LabelGou.App.Export;

/// <summary>
/// 把渲染/打印这类活丢到独立的 STA 线程上跑。
/// <para>
/// 为什么不能直接用 <c>Task.Run</c>：WPF 的 DrawingVisual、RenderTargetBitmap、PrintDialog 都是线程亲和对象，
/// 而打印假脱机接口（System.Printing）要求调用线程是 STA。放在后台线程做的原因是 263 页的导出会把 UI 冻住，
/// 用户会以为程序挂了。
/// </para>
/// </summary>
public static class StaWorker
{
    public static Task<T> RunAsync<T>(Func<IProgress<string>, CancellationToken, T> work, Action<string>? report, CancellationToken token)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        // Progress<T> 必须在调用方（UI 线程）构造，它抓的是构造时所在的同步上下文，这样回调才会上屏。
        var progress = new Progress<string>(text => report?.Invoke(text));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(work(progress, token));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "LabelGou-Output",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
