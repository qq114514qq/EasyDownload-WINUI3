using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using EasyDownload.Services;

namespace EasyDownload.Views;

/// <summary>
/// 首次运行引导（OOBE 风格）。做成主窗口里的覆盖层而不是新开 Window，
/// 因为 WinUI 3 多窗口要处理 AppWindow，容易出各种坑。
/// 完成后写 FirstRunDone=true，第二次启动不再显示。
/// </summary>
public sealed partial class FirstRunPage : UserControl
{
    private readonly MainWindow _main;
    private int _step;

    private readonly List<StackPanel> _steps;
    private readonly List<Border> _dots;

    public FirstRunPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();

        _steps = new List<StackPanel> { Step0, Step1, Step2, Step3 };
        _dots = new List<Border> { Dot0, Dot1, Dot2, Dot3 };

        WizThreadSlider.Value = Math.Clamp(_main.Settings.MaxThreads, 1, 32);
        WizThreadText.Text = ((int)WizThreadSlider.Value).ToString();
        WizFolderBox.Text = _main.Settings.DownloadFolder;

        ShowStep(0);
    }

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, 3);

        for (int i = 0; i < _steps.Count; i++)
            _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;

        StepText.Text = $"第 {_step + 1} 步 / 共 4 步";

        try
        {
            var accent = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            var dim = (SolidColorBrush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
            for (int i = 0; i < _dots.Count; i++)
                _dots[i].Background = i == _step ? accent : dim;
        }
        catch { }

        PrevBtn.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Content = _step == 3 ? "开始使用" : "下一步";
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            ShowStep(_step + 1);
            return;
        }
        Finish();
    }

    private void Finish()
    {
        var s = _main.Settings;
        s.MaxThreads = (int)WizThreadSlider.Value;

        var dir = WizFolderBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(dir))
        {
            s.DownloadFolder = dir;
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
        }

        SettingsService.Save(s);
        _main.FinishFirstRun();
    }

    // ---------- 主题 ----------

    private void PickLight_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.Theme = "Light";
        _main.ApplyTheme();
    }

    private void PickDark_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.Theme = "Dark";
        _main.ApplyTheme();
    }

    private void WizThread_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (WizThreadText != null) WizThreadText.Text = ((int)WizThreadSlider.Value).ToString();
    }

    private async void WizBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_main);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !string.IsNullOrWhiteSpace(folder.Path))
                WizFolderBox.Text = folder.Path;
        }
        catch { }
    }
}
