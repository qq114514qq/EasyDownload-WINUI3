using System;
using Microsoft.UI.Dispatching;

namespace EasyDownload.Helpers;

/// <summary>
/// WinUI 3 没有 WPF 的 Application.Current.Dispatcher，
/// 这里在 App 启动时抓取界面线程的 DispatcherQueue，之后后台线程一律通过它回界面。
/// </summary>
public static class UiDispatcher
{
    private static DispatcherQueue _queue;

    public static void Init()
    {
        _queue ??= DispatcherQueue.GetForCurrentThread();
    }

    public static bool HasThreadAccess => _queue == null || _queue.HasThreadAccess;

    /// <summary>在界面线程执行；已经在界面线程则直接跑</summary>
    public static void Run(Action action)
    {
        if (action == null) return;
        if (_queue == null || _queue.HasThreadAccess)
        {
            action();
            return;
        }
        _queue.TryEnqueue(() => action());
    }
}
