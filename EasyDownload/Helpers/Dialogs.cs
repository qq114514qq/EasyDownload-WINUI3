using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyDownload.Helpers;

/// <summary>WinUI 3 没有 MessageBox，统一用 ContentDialog（需要 XamlRoot）</summary>
public static class Dialogs
{
    public static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        if (root == null) return;
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = root
        };
        try { await dlg.ShowAsync().AsTask(); } catch { }
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message)
    {
        if (root == null) return true;
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "是",
            CloseButtonText = "否",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };
        try
        {
            var r = await dlg.ShowAsync().AsTask();
            return r == ContentDialogResult.Primary;
        }
        catch { return true; }
    }

    public static async Task<string> InputAsync(XamlRoot root, string title, string message, string defaultValue)
    {
        if (root == null) return null;

        var box = new TextBox
        {
            Text = defaultValue ?? "",
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = message ?? "",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(box);

        var dlg = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        try
        {
            var r = await dlg.ShowAsync().AsTask();
            return r == ContentDialogResult.Primary ? box.Text.Trim() : null;
        }
        catch { return null; }
    }
}
