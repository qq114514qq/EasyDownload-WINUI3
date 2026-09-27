using Microsoft.UI.Xaml;
using EasyDownload.Helpers;
using EasyDownload.Services;

namespace EasyDownload;

public partial class App : Application
{
    public static MainWindow AppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 后台线程要回界面线程改属性，先把界面线程的 DispatcherQueue 记下来
        UiDispatcher.Init();

        var settings = SettingsService.Load();
        AppWindow = new MainWindow(settings);
        AppWindow.Activate();
    }
}
