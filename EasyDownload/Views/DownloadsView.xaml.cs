using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using EasyDownload.Helpers;
using EasyDownload.Models;

namespace EasyDownload.Views;

public sealed partial class DownloadsView : UserControl
{
    private readonly MainWindow _main;

    public DownloadsView(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        List.ItemsSource = _main.Downloader.Items;
    }

    private DownloadItem ItemOf(object sender)
        => ((FrameworkElement)sender).DataContext as DownloadItem;

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        var item = _main.Downloader.Add(url);

        if (_main.Settings.ConfirmBeforeDownload)
        {
            var yes = await Dialogs.ConfirmAsync(XamlRoot, "下载确认",
                $"{item.FileName}\n\n{url}");
            if (!yes)
            {
                _main.Downloader.Delete(item);
                UrlBox.Text = "";
                return;
            }
        }

        _main.Downloader.Start(item);
        UrlBox.Text = "";
    }

    private void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            New_Click(sender, e);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is DownloadItem it) _main.Downloader.Start(it);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is DownloadItem it) _main.Downloader.Pause(it);
    }

    /// <summary>服务器死活不给真名时的兜底：手动改成正确的名字（记得带扩展名）</summary>
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not DownloadItem it) return;

        if (it.IsRunning)
        {
            await Dialogs.MessageAsync(XamlRoot, "无法改名", "这个任务正在下载，请先点「暂停」再改名。");
            return;
        }

        var name = await Dialogs.InputAsync(XamlRoot, "重命名",
            "请输入新的文件名（记得带扩展名，例如 setup.exe）", it.FileName);

        if (string.IsNullOrWhiteSpace(name)) return;

        if (!_main.Downloader.Rename(it, name))
            await Dialogs.MessageAsync(XamlRoot, "无法改名", "改名失败，请检查文件名是否合法。");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is DownloadItem it) _main.Downloader.Delete(it);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not DownloadItem it) return;
        try
        {
            if (File.Exists(it.SavePath))
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{it.SavePath}\"",
                    UseShellExecute = true
                });
            else if (Directory.Exists(_main.Settings.DownloadFolder))
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _main.Settings.DownloadFolder,
                    UseShellExecute = true
                });
        }
        catch { }
    }
}
