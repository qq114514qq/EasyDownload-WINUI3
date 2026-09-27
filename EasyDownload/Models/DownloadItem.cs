using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using EasyDownload.Helpers;

namespace EasyDownload.Models;

public class DownloadItem : INotifyPropertyChanged
{
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SavePath { get; set; } = "";

    /// <summary>正在写的文件（.part）。改名成 SavePath 前都写它</summary>
    public string TempPath
    {
        get
        {
            var dir = Path.GetDirectoryName(SavePath) ?? "";
            var name = Path.GetFileName(SavePath);
            return Path.Combine(dir, name + ".part");
        }
    }

    /// <summary>服务器是否支持断点续传</summary>
    public bool SupportsRange { get; set; }

    private long _totalBytes;
    public long TotalBytes
    {
        get => _totalBytes;
        set { _totalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeText)); }
    }

    private long _receivedBytes;
    public long ReceivedBytes
    {
        get => _receivedBytes;
        set { _receivedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeText)); }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(PercentText)); }
    }

    /// <summary>服务器不给文件大小时，进度条转圈，别卡在 0% 吓人</summary>
    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set { _isIndeterminate = value; OnPropertyChanged(); OnPropertyChanged(nameof(PercentText)); }
    }

    private string _status = "等待中";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _speedText = "";
    public string SpeedText
    {
        get => _speedText;
        set { _speedText = value; OnPropertyChanged(); }
    }

    private string _etaText = "";
    /// <summary>剩余时间，如 "3 分 20 秒"</summary>
    public string EtaText
    {
        get => _etaText;
        set { _etaText = value; OnPropertyChanged(); }
    }

    /// <summary>进度百分比文字，如 "47%"</summary>
    public string PercentText
    {
        get
        {
            if (IsIndeterminate) return "";
            return ((int)Math.Min(100, Math.Max(0, Progress))).ToString() + "%";
        }
    }

    public string SizeText => $"{Format(ReceivedBytes)} / {Format(TotalBytes)}";

    /// <summary>后台任务是否真的在跑（不是靠状态文字猜）</summary>
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); }
    }

    public bool IsActive => IsRunning;

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Threading.CancellationTokenSource? Cts { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Diagnostics.Stopwatch? Stopwatch { get; set; }

    // ---- 滑窗测速：用"最近这段时间的速度 + 指数平滑"，剩余时间才不会乱跳 ----

    [System.Text.Json.Serialization.JsonIgnore]
    public long LastSampleBytes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double LastSampleSeconds { get; set; }

    /// <summary>平滑后的速度（字节/秒）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double EmaSpeed { get; set; }

    /// <summary>开始下载 / 断点续传续上后调用，避免把已有字节算成瞬时速度</summary>
    public void ResetSpeed()
    {
        LastSampleBytes = System.Threading.Interlocked.Read(ref _receivedBytes);
        LastSampleSeconds = 0;
        EmaSpeed = 0;
    }

    /// <summary>
    /// 多线程累加字节数。这里只做计数，界面刷新统一交给定时器（300ms 一次），
    /// 否则 32 个线程每读一块就通知一次界面，会把 UI 刷爆。
    /// </summary>
    public void AddReceived(long n)
    {
        System.Threading.Interlocked.Add(ref _receivedBytes, n);
    }

    /// <summary>把秒数格式化成中文可读的剩余时间</summary>
    public static string FormatTime(long seconds)
    {
        if (seconds <= 0) return "0 秒";
        var ts = System.TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes} 分 {ts.Seconds} 秒";
        return $"{ts.Seconds} 秒";
    }

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] unit = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < unit.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {unit[i]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;

        // 后台线程改的属性，统一丢回界面线程，保证 UI 一定刷新
        try
        {
            if (!UiDispatcher.HasThreadAccess)
            {
                UiDispatcher.Run(() =>
                    handler.Invoke(this, new PropertyChangedEventArgs(name)));
                return;
            }
        }
        catch { }

        handler.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
