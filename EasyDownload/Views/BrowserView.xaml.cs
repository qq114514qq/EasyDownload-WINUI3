using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using EasyDownload.Helpers;
using EasyDownload.Models;

namespace EasyDownload.Views;

public sealed partial class BrowserView : UserControl
{
    private readonly MainWindow _main;
    private AppSettings S => _main.Settings;

    /// <summary>标签刚建好还没进可视树，先把网址存这，Loaded 后再导航</summary>
    private readonly Dictionary<TabViewItem, string> _pending = new();

    public BrowserView(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        AddTab(S.HomePage);
    }

    private WebView2 CurrentWebView()
        => (Tabs.SelectedItem as TabViewItem)?.Content as WebView2;

    // ---------- 标签 ----------

    public void AddTab(string url = null)
    {
        var wv = new WebView2();
        wv.CoreWebView2Initialized += WebView_Ready;

        var tab = new TabViewItem
        {
            Header = "新标签页",
            Content = wv
        };

        if (!string.IsNullOrWhiteSpace(url))
        {
            _pending[tab] = url;
            tab.Loaded += Tab_Loaded;
        }

        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    private async void Tab_Loaded(object sender, RoutedEventArgs e)
    {
        var tab = (TabViewItem)sender;
        tab.Loaded -= Tab_Loaded;
        if (!_pending.TryGetValue(tab, out var url)) return;
        _pending.Remove(tab);

        await Task.Delay(30);   // 等一帧，确保已经挂到可视树上
        if (tab.Content is WebView2 wv)
            await NavigateAsync(wv, url);
    }

    private void Tabs_AddTabClick(TabView sender, object args)
        => AddTab(S.HomePage);

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab?.Content is WebView2 wv)
        {
            Tabs.TabItems.Remove(args.Tab);
            try { wv.Close(); } catch { }
        }
        if (Tabs.TabItems.Count == 0) AddTab(S.HomePage);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var wv = CurrentWebView();
        if (wv == null) return;

        try
        {
            if (wv.CoreWebView2 != null)
                AddressBox.Text = wv.CoreWebView2.Source;
        }
        catch { }
    }

    // ---------- WebView2 ----------

    private void WebView_Ready(WebView2 wv, CoreWebView2InitializedEventArgs e)
    {
        if (wv?.CoreWebView2 == null) return;
        var core = wv.CoreWebView2;

        // 网页里点下载 → 交给自家下载引擎
        core.DownloadStarting += Core_DownloadStarting;

        core.SourceChanged += (_, _) =>
        {
            UiDispatcher.Run(() =>
            {
                if (ReferenceEquals(wv, CurrentWebView()))
                    AddressBox.Text = core.Source;
            });
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            var t = core.DocumentTitle;
            UiDispatcher.Run(() =>
            {
                foreach (var o in Tabs.TabItems)
                    if (o is TabViewItem ti && ReferenceEquals(ti.Content, wv))
                        ti.Header = string.IsNullOrWhiteSpace(t) ? "新标签页" : t;
            });
        };

        core.NewWindowRequested += (_, b) =>
        {
            b.Handled = true;
            UiDispatcher.Run(() => AddTab(b.Uri));
        };
    }

    /// <summary>
    /// 网页下载接管：先让自家下载器接住，成功后再取消 Edge 自己的下载；
    /// 万一家这边挂了，就放开让 Edge 正常下，不至于两头都丢。
    /// </summary>
    private void Core_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            string url = args.DownloadOperation?.Uri ?? "";
            string file = args.ResultFilePath ?? "";
            if (string.IsNullOrWhiteSpace(url)) return;

            bool ok = false;
            UiDispatcher.Run(() =>
            {
                try
                {
                    var item = _main.Downloader.Add(url, file);
                    _main.Downloader.Start(item);
                    ok = true;
                }
                catch { ok = false; }
            });

            args.Handled = ok;
            if (ok)
            {
                try { args.DownloadOperation?.Cancel(); } catch { }
                UiDispatcher.Run(() => _main.GoDownloads());
            }
        }
        catch { }
    }

    // ---------- 导航 ----------

    private async Task NavigateAsync(WebView2 wv, string url)
    {
        try
        {
            await wv.EnsureCoreWebView2Async();
            wv.Source = new Uri(Normalize(url));
        }
        catch { }
    }

    private static string Normalize(string url)
    {
        var u = (url ?? "").Trim();
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u;
        return u;
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        var wv = CurrentWebView();
        if (wv != null) _ = NavigateAsync(wv, AddressBox.Text);
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var wv = CurrentWebView();
            if (wv != null) _ = NavigateAsync(wv, AddressBox.Text);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var core = CurrentWebView()?.CoreWebView2;
        if (core != null && core.CanGoBack) core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        var core = CurrentWebView()?.CoreWebView2;
        if (core != null && core.CanGoForward) core.GoForward();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => CurrentWebView()?.CoreWebView2?.Reload();
}
