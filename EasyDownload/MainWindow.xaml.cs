using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using EasyDownload.Models;
using EasyDownload.Services;
using EasyDownload.Views;

namespace EasyDownload;

public sealed partial class MainWindow : Window
{
    public AppSettings Settings { get; }
    public DownloadService Downloader { get; }

    private readonly DispatcherQueueTimer _timer;

    private BrowserView _browser;
    private DownloadsView _downloads;
    private SettingsView _settings;

    public MainWindow(AppSettings settings)
    {
        Settings = settings;
        Downloader = new DownloadService(settings);

        InitializeComponent();

        // Mica 云母材质（Windows 11）。Win10 上不支持会被 catch 掉，不影响运行
        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        // 自定义标题栏
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch { }

        ApplyTheme();
        Navigate("home");

        // 进度 / 速度 / 剩余时间刷新
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Tick += (_, _) => Downloader.Tick();
        _timer.Start();

        // 第一次用弹引导，之后不再弹
        if (!settings.FirstRunDone)
            ShowFirstRun();
    }

    // ---------- 主题 ----------

    public void ApplyTheme()
    {
        ThemeService.ApplyTo(Root, Settings.Theme);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        if (ThemeIcon == null) return;
        ThemeIcon.Glyph = Settings.Theme == "Dark" ? "\uE708" : "\uE706";  // 月亮 / 太阳
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.Theme = Settings.Theme == "Dark" ? "Light" : "Dark";
        ApplyTheme();
        SettingsService.Save(Settings);
        _settings?.Reload();
    }

    // ---------- 导航 ----------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    public void Navigate(string tag)
    {
        switch (tag)
        {
            case "downloads":
                _downloads ??= new DownloadsView(this);
                Host.Content = _downloads;
                break;
            case "settings":
                _settings ??= new SettingsView(this);
                Host.Content = _settings;
                break;
            default:
                _browser ??= new BrowserView(this);
                Host.Content = _browser;
                break;
        }
    }

    public void GoDownloads()
    {
        try
        {
            Nav.SelectedItem = NavDownloads;
            Navigate("downloads");
        }
        catch { }
    }

    // ---------- 设置生效 ----------

    public void ApplySettings()
    {
        Downloader.UpdateSettings(Settings);
        _settings?.Reload();
        ApplyTheme();
    }

    // ---------- 首次引导 ----------

    public void ShowFirstRun()
    {
        FirstRunHost.Children.Clear();
        FirstRunHost.Children.Add(new FirstRunPage(this));
        FirstRunHost.Visibility = Visibility.Visible;
    }

    public void FinishFirstRun()
    {
        FirstRunHost.Children.Clear();
        FirstRunHost.Visibility = Visibility.Collapsed;
        Settings.FirstRunDone = true;
        SettingsService.Save(Settings);
        ApplySettings();
    }
}
