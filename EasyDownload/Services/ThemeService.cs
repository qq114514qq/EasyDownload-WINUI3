using Microsoft.UI.Xaml;

namespace EasyDownload.Services;

public static class ThemeService
{
    public static ElementTheme ToElementTheme(string theme)
        => string.Equals(theme, "Dark", System.StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Dark
            : ElementTheme.Light;

    /// <summary>
    /// WinUI 3 的深浅主题：直接设根元素的 RequestedTheme，
    /// 所有 {ThemeResource} 画笔会自动切换，不用像 WPF 那样换资源字典。
    /// </summary>
    public static void ApplyTo(FrameworkElement root, string theme)
    {
        if (root == null) return;
        root.RequestedTheme = ToElementTheme(theme);
    }
}
