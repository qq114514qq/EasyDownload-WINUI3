using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EasyDownload.Services;

namespace EasyDownload.Views;

public sealed partial class SettingsView : UserControl
{
    private readonly MainWindow _main;

    public SettingsView(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        Reload();
    }

    public void Reload()
    {
        var s = _main.Settings;
        ThreadSlider.Value = Math.Clamp(s.MaxThreads, 1, 32);
        ConcurrentSlider.Value = Math.Clamp(s.MaxConcurrent, 1, 8);
        ThrottleSwitch.IsOn = s.AutoThrottle;
        ResumeSwitch.IsOn = s.ResumeDownload;
        ConfirmSwitch.IsOn = s.ConfirmBeforeDownload;
        FolderBox.Text = s.DownloadFolder;
        ThreadText.Text = s.MaxThreads.ToString();
        ConcurrentText.Text = s.MaxConcurrent.ToString();
        UpdateThemeButtons();
        TipText.Text = "";
    }

    private void UpdateThemeButtons()
    {
        try
        {
            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            LightBtn.Style = _main.Settings.Theme == "Light" ? accent : null;
            DarkBtn.Style = _main.Settings.Theme == "Dark" ? accent : null;
        }
        catch { }
    }

    private void Light_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.Theme = "Light";
        _main.ApplyTheme();
        UpdateThemeButtons();
    }

    private void Dark_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.Theme = "Dark";
        _main.ApplyTheme();
        UpdateThemeButtons();
    }

    private void Thread_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ThreadText != null) ThreadText.Text = ((int)ThreadSlider.Value).ToString();
    }

    private void Concurrent_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ConcurrentText != null) ConcurrentText.Text = ((int)ConcurrentSlider.Value).ToString();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_main.Settings.DownloadFolder))
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _main.Settings.DownloadFolder,
                    UseShellExecute = true
                });
        }
        catch { }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeFilter.Add("*");

            // 桌面应用（非打包）用文件选择器必须绑定窗口句柄
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_main);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !string.IsNullOrWhiteSpace(folder.Path))
                FolderBox.Text = folder.Path;
        }
        catch
        {
            TipText.Text = "选择器不可用，请手动输入路径";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _main.Settings;
        s.MaxThreads = (int)ThreadSlider.Value;
        s.MaxConcurrent = (int)ConcurrentSlider.Value;
        s.AutoThrottle = ThrottleSwitch.IsOn;
        s.ResumeDownload = ResumeSwitch.IsOn;
        s.ConfirmBeforeDownload = ConfirmSwitch.IsOn;

        var dir = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            TipText.Text = "下载目录不能为空";
            return;
        }
        s.DownloadFolder = dir;
        try { Directory.CreateDirectory(dir); } catch { }

        SettingsService.Save(s);
        _main.ApplySettings();

        TipText.Text = "已保存";
    }
}
