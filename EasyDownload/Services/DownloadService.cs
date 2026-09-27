using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using EasyDownload.Helpers;
using EasyDownload.Models;

namespace EasyDownload.Services;

public class DownloadService
{
    public ObservableCollection<DownloadItem> Items { get; } = new();

    private AppSettings _s;
    private SemaphoreSlim _gate;

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // 允许足够多的并发连接，否则 32 线程会排队等连接
        MaxConnectionsPerServer = 128,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        AutomaticDecompression = System.Net.DecompressionMethods.None
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    static DownloadService()
    {
        try
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        }
        catch { }
    }

    public DownloadService(AppSettings s)
    {
        _s = s;
        _gate = new SemaphoreSlim(Math.Max(1, s.MaxConcurrent));
    }

    public void UpdateSettings(AppSettings s)
    {
        _s = s;
    }

    // ---------- 管理 ----------

    public DownloadItem Add(string url, string? suggestedPath = null)
    {
        var name = string.IsNullOrWhiteSpace(suggestedPath) ? NameFromUrl(url) : Path.GetFileName(suggestedPath);
        if (string.IsNullOrWhiteSpace(name)) name = "下载_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

        try { Directory.CreateDirectory(_s.DownloadFolder); } catch { }

        var item = new DownloadItem
        {
            Url = url,
            FileName = name,
            SavePath = Path.Combine(_s.DownloadFolder, name)
        };
        Items.Add(item);
        return item;
    }

    /// <summary>手动改名：只能在没开始下载时改，避免改坏正在写的文件</summary>
    public bool Rename(DownloadItem item, string newName)
    {
        newName = Sanitize((newName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(newName)) return false;
        if (item.IsRunning) return false;

        item.FileName = newName;
        item.SavePath = UniquePath(Path.Combine(_s.DownloadFolder, newName));
        return true;
    }

    public void Start(DownloadItem item)
    {
        if (item.IsRunning) return;
        if (item.Status == "已完成") { item.Progress = 100; return; }

        item.IsRunning = true;
        item.Cts = new CancellationTokenSource();
        item.Stopwatch = Stopwatch.StartNew();
        item.Status = "等待中";
        item.IsIndeterminate = false;
        item.ResetSpeed();
        _ = Task.Run(() => RunAsync(item));
    }

    public void Pause(DownloadItem item)
    {
        try { item.Cts?.Cancel(); } catch { }
        item.Status = "已暂停";
        item.SpeedText = "";
    }

    public void Delete(DownloadItem item)
    {
        try { item.Cts?.Cancel(); } catch { }
        try { if (File.Exists(item.TempPath)) File.Delete(item.TempPath); } catch { }
        try { if (File.Exists(item.TempPath + ".prog")) File.Delete(item.TempPath + ".prog"); } catch { }
        try { if (Directory.Exists(item.TempPath + ".parts")) Directory.Delete(item.TempPath + ".parts", true); } catch { }
        item.IsRunning = false;
        Items.Remove(item);
    }

    public void ClearFinished()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].Status == "已完成")
                Items.RemoveAt(i);
    }

    /// <summary>由界面定时器调用：刷新进度 / 速度 / 剩余时间</summary>
    public void Tick()
    {
        foreach (var it in Items)
        {
            if (it.TotalBytes > 0)
                it.Progress = Math.Min(100, it.ReceivedBytes * 100.0 / it.TotalBytes);

            if (it.IsRunning && it.Stopwatch != null)
            {
                var sec = it.Stopwatch.Elapsed.TotalSeconds;
                var cur = it.ReceivedBytes;

                // 速度用「最近窗口 + 指数平滑」，不再用全程平均值
                // （全程平均值在断点续传、多线程收尾时会失真，导致剩余时间乱跳）
                var dt = sec - it.LastSampleSeconds;
                if (dt >= 0.4)
                {
                    var inst = (cur - it.LastSampleBytes) / dt;
                    if (inst < 0) inst = 0;
                    it.EmaSpeed = it.EmaSpeed <= 0 ? inst : (it.EmaSpeed * 0.65 + inst * 0.35);
                    it.LastSampleBytes = cur;
                    it.LastSampleSeconds = sec;
                }

                var spd = it.EmaSpeed > 0 ? it.EmaSpeed : (sec > 0.5 ? cur / sec : 0);
                if (spd > 0) it.SpeedText = DownloadItem.Format((long)spd) + "/s";

                if (it.TotalBytes > 0 && spd > 1024)
                {
                    var left = (long)((it.TotalBytes - cur) / spd);
                    if (left > 0 && left < 100 * 24 * 3600)
                        it.EtaText = "剩余 " + DownloadItem.FormatTime(left);
                    else if (left <= 0)
                        it.EtaText = "即将完成";
                }
            }
            else if (!it.IsRunning)
            {
                it.EtaText = "";
            }
            it.OnPropertyChanged(nameof(it.SizeText));
        }
    }

    // ---------- 引擎 ----------

    private async Task RunAsync(DownloadItem item)
    {
        var ct = item.Cts!.Token;
        bool acquired = false;
        string? part = null;

        try
        {
            await _gate.WaitAsync(ct);
            acquired = true;

            item.Status = "连接中";

            // 一次探测拿全：大小 / 是否支持断点 / 真实文件名 / 文件类型 / 文件头魔数
            var info = await ProbeAsync(item.Url, ct);
            item.TotalBytes = info.Total > 0 ? info.Total : 0;
            item.IsIndeterminate = item.TotalBytes <= 0;
            item.SupportsRange = info.SupportsRange && info.Total > 0;

            // 定文件名（此时还没开始写盘，改名安全）
            ResolveName(item, info);

            part = item.TempPath;
            item.Status = "下载中";

            if (item.SupportsRange && item.TotalBytes > 512 * 1024 && _s.MaxThreads > 1)
                await MultiAsync(item, part, _s.MaxThreads, ct);
            else
                await SingleAsync(item, part, ct);

            // 收尾：不再拷贝合并，直接把 .part 改名成正式文件（同盘改名是瞬时的）
            item.Status = "完成中";
            var final = CommitFile(item, part);

            item.Progress = 100;
            item.IsIndeterminate = false;
            item.ReceivedBytes = item.TotalBytes > 0 ? item.TotalBytes : item.ReceivedBytes;
            item.Status = "已完成";
            item.SpeedText = "";
            item.EtaText = "";
            item.SavePath = final;
            item.FileName = Path.GetFileName(final);
        }
        catch (OperationCanceledException)
        {
            item.Status = "已暂停";
            item.SpeedText = "";
            item.IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            item.Status = "失败：" + ex.Message;
            item.SpeedText = "";
            item.IsIndeterminate = false;
        }
        finally
        {
            item.IsRunning = false;
            item.IsIndeterminate = false;
            if (acquired)
            {
                try { _gate.Release(); } catch { }
            }
        }
    }

    /// <summary>探测结果</summary>
    private sealed class ProbeInfo
    {
        public long Total = -1;
        public bool SupportsRange;
        public string? CdName;
        public string? ContentType;
        public string? FinalUrl;
        public byte[] Magic = Array.Empty<byte>();
        public bool Ok;
    }

    private async Task<ProbeInfo> ProbeAsync(string url, CancellationToken ct)
    {
        var info = new ProbeInfo();

        try
        {
            // 不带 Range 地发一次 GET：这样服务器才会把 Content-Disposition 真实文件名吐出来
            // （带 Range 时很多 CDN 会省略这个头，这正是以前名字是乱码的根因）
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            info.Ok = true;
            info.FinalUrl = resp.RequestMessage?.RequestUri?.ToString();
            info.Total = resp.Content.Headers.ContentLength ?? -1;
            info.SupportsRange = resp.Headers.AcceptRanges.Contains("bytes");
            info.ContentType = resp.Content.Headers.ContentType?.MediaType;
            if (resp.Content.Headers.TryGetValues("Content-Disposition", out var v))
                info.CdName = ParseContentDisposition(string.Join(";", v));

            // 顺手读前 512 字节：靠文件头判断真实类型（MZ → exe，PK → zip …）
            try
            {
                var s = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[512];
                int got = 0;
                while (got < buf.Length)
                {
                    int r = await s.ReadAsync(buf.AsMemory(got, buf.Length - got), ct);
                    if (r <= 0) break;
                    got += r;
                }
                info.Magic = buf.AsSpan(0, got).ToArray();
            }
            catch { }
        }
        catch { }

        if (!info.Ok)
        {
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var hr = await _http.SendAsync(head, ct);
                info.Ok = true;
                info.FinalUrl = hr.RequestMessage?.RequestUri?.ToString();
                info.Total = hr.Content.Headers.ContentLength ?? -1;
                info.SupportsRange = hr.Headers.AcceptRanges.Contains("bytes");
                info.ContentType = hr.Content.Headers.ContentType?.MediaType;
                if (hr.Content.Headers.TryGetValues("Content-Disposition", out var v2))
                    info.CdName = ParseContentDisposition(string.Join(";", v2));
            }
            catch { }
        }

        return info;
    }

    // ---------- 多线程：预分配单文件 + 定点写入，不再产生 32 个分块文件 ----------

    private async Task MultiAsync(DownloadItem item, string part, int n, CancellationToken ct)
    {
        long total = item.TotalBytes;
        long block = total / n;
        long limit = _s.AutoThrottle ? Math.Max(64 * 1024, (1024 * 1024) / Math.Max(1, n)) : 0;

        try { Directory.CreateDirectory(Path.GetDirectoryName(part)!); } catch { }

        // 预分配：一次性把文件撑到最终大小（NTFS 上是稀疏的，不真的写零）
        using (var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write))
        {
            if (fs.Length != total)
            {
                try { fs.SetLength(total); } catch { }
            }
        }

        // 断点续传：读回每一块已经下了多少
        var done = LoadProgress(part + ".prog", n);
        long already = 0;
        foreach (var d in done) already += d;
        item.ReceivedBytes = Math.Min(already, total);
        item.ResetSpeed();

        var tasks = new List<Task>();
        for (int i = 0; i < n; i++)
        {
            long start = i * block;
            long end = (i == n - 1) ? total - 1 : (start + block - 1);
            tasks.Add(PartAsync(item, part, i, start, end, Math.Min(done[i], end - start + 1), limit, ct));
        }

        await Task.WhenAll(tasks);
        try { File.Delete(part + ".prog"); } catch { }
    }

    private async Task PartAsync(DownloadItem item, string part, int index, long start, long end,
                                 long already, long limit, CancellationToken ct)
    {
        string prog = part + ".prog";
        long written = already;              // 本块已完成的字节
        long lastSave = written;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            long from = start + written;
            if (from > end) return;          // 这块早就下完了

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
                req.Headers.Range = new RangeHeaderValue(from, end);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                Stream src = await resp.Content.ReadAsStreamAsync(ct);
                if (limit > 0) src = new ThrottleStream(src, limit);

                // 定点写：每个线程只写自己那一段，不用合并，也不会互相覆盖
                using var handle = File.OpenHandle(part, FileMode.Open, FileAccess.Write, FileShare.Write);

                var buf = new byte[262144];  // 256KB，比以前的 80KB 少很多次系统调用
                long offset = from;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await RandomAccess.WriteAsync(handle, buf.AsMemory(0, read), offset, ct);
                    offset += read;
                    written += read;
                    item.AddReceived(read);

                    // 每 4MB 记一次进度，暂停后能从这里接着下
                    if (written - lastSave >= 4 * 1024 * 1024)
                    {
                        SaveProgress(prog, index, written);
                        lastSave = written;
                    }
                }
                await src.DisposeAsync();

                SaveProgress(prog, index, end - start + 1);
                return;
            }
            catch (OperationCanceledException)
            {
                SaveProgress(prog, index, written);
                throw;
            }
            catch (Exception)
            {
                if (attempt == 2) { SaveProgress(prog, index, written); throw; }
                try { await Task.Delay(400 * (attempt + 1), ct); } catch { }
            }
        }
    }

    /// <summary>服务器不给大小或不支持断点：单线程顺着写</summary>
    private async Task SingleAsync(DownloadItem item, string part, CancellationToken ct)
    {
        long limit = _s.AutoThrottle ? 1024 * 1024 : 0;

        try { Directory.CreateDirectory(Path.GetDirectoryName(part)!); } catch { }

        using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        if (item.TotalBytes <= 0)
            item.TotalBytes = resp.Content.Headers.ContentLength ?? 0;
        item.IsIndeterminate = item.TotalBytes <= 0;

        Stream src = await resp.Content.ReadAsStreamAsync(ct);
        if (limit > 0) src = new ThrottleStream(src, limit);

        using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 262144);
        var buf = new byte[262144];
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            item.AddReceived(read);
        }
        await src.DisposeAsync();
    }

    /// <summary>把 .part 正式改名成目标文件。同盘改名是瞬时操作，不再有"卡在 99% 慢慢合并"</summary>
    private static string CommitFile(DownloadItem item, string part)
    {
        var target = UniquePath(Path.Combine(Path.GetDirectoryName(part)!, item.FileName));
        try
        {
            if (File.Exists(target)) { try { File.Delete(target); } catch { } }
            File.Move(part, target);
        }
        catch
        {
            // 改名失败（跨盘等）就退回拷贝，至少保证文件能用
            using (var src = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576))
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1048576))
            {
                src.CopyTo(dst);
            }
            try { File.Delete(part); } catch { }
        }
        return target;
    }

    // ---------- 断点进度记录 ----------

    private static long[] LoadProgress(string path, int n)
    {
        var arr = new long[n];
        try
        {
            if (File.Exists(path))
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[n * 8];
                int got = RandomAccess.Read(h, buf, 0);
                for (int i = 0; i < n && (i + 1) * 8 <= got; i++)
                {
                    var v = BitConverter.ToInt64(buf, i * 8);
                    if (v > 0) arr[i] = v;
                }
            }
        }
        catch { }
        return arr;
    }

    private static void SaveProgress(string path, int index, long value)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write);
            var need = (index + 1) * 8;
            if (RandomAccess.GetLength(h) < need)
            {
                try { RandomAccess.SetLength(h, need); } catch { }
            }
            RandomAccess.Write(h, BitConverter.GetBytes(value), index * 8L);
        }
        catch { }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 999; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        return path;
    }

    // ================= 文件名 =================

    /// <summary>
    /// 决定最终文件名。优先级：
    /// Content-Disposition ＞ 重定向后地址 ＞ 原始地址 ＞ 浏览器给的名字；
    /// 名字没扩展名或像乱码时，先用 Content-Type 补，再用文件头魔数补（MZ → .exe）。
    /// </summary>
    private void ResolveName(DownloadItem item, ProbeInfo info)
    {
        try
        {
            string? best = null;

            // 0) 浏览器接管、或链接末尾自带的名字，只要像样（有扩展名、不是乱码）就别去动它
            if (!LooksLikeJunk(item.FileName) && Path.HasExtension(item.FileName))
                return;

            // 1) 服务器给的真名最权威
            if (!string.IsNullOrWhiteSpace(info.CdName)) best = info.CdName;

            // 2) 重定向后的最终地址
            if (LooksLikeJunk(best) && !string.IsNullOrWhiteSpace(info.FinalUrl))
                best = NameFromUrl(info.FinalUrl!);

            // 3) 原始地址
            if (LooksLikeJunk(best))
                best = NameFromUrl(item.Url);

            // 4) 浏览器接管时给的名字（有扩展名、不是乱码才用）
            if (LooksLikeJunk(best) && !LooksLikeJunk(item.FileName) && Path.HasExtension(item.FileName))
                best = item.FileName;

            // 5) 实在没有：兜底名
            if (LooksLikeJunk(best))
                best = "下载_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            if (best!.Length > 120) best = best.Substring(0, 120);

            // 补扩展名：先看 Content-Type，再看文件头魔数
            if (!Path.HasExtension(best) || LooksLikeJunk(best))
            {
                var ext = ExtFromContentType(info.ContentType) ?? ExtFromMagic(info.Magic);
                if (ext != null)
                {
                    if (LooksLikeJunk(best)) best = CleanJunk(best);
                    best += ext;
                }
            }

            best = Sanitize(best);
            if (string.IsNullOrWhiteSpace(best)) return;
            if (best == item.FileName) return;

            item.FileName = best;
            item.SavePath = UniquePath(Path.Combine(_s.DownloadFolder, best));
        }
        catch { }
    }

    /// <summary>解析 Content-Disposition：优先 filename*（RFC 5987，UTF-8），其次 filename（可能是 GBK）</summary>
    private static string? ParseContentDisposition(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        // filename*=UTF-8''%E4%B8%AD%E6%96%87.exe
        var m = Regex.Match(header, "filename\\*\\s*=\\s*[^']*'[^']*'([^;]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var raw = m.Groups[1].Value.Trim().Trim('"');
            var dec = SafeUnescape(raw);
            if (!string.IsNullOrWhiteSpace(dec)) return dec;
        }

        m = Regex.Match(header, "filename\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(header, "filename\\s*=\\s*([^;]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var val = m.Groups[1].Value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(val)) return null;

        var utf8 = SafeUnescape(val);
        // 解出一堆替换字符（U+FFFD），说明它其实不是 UTF-8，改按 GB18030 试一次
        if (ContainsPercent(val) && CountReplacement(utf8) > 0)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var gbk = Encoding.GetEncoding("GB18030");
                var decoded = gbk.GetString(PercentToBytes(val));
                if (!string.IsNullOrWhiteSpace(decoded) && CountReplacement(decoded) < CountReplacement(utf8))
                    return decoded;
            }
            catch { }
        }
        return utf8;
    }

    /// <summary>按文件头判断真实类型 —— 服务器不给名字时这是最靠谱的依据</summary>
    private static string? ExtFromMagic(byte[] h)
    {
        if (h == null || h.Length < 4) return null;

        // MZ：exe / dll / msi 中的 exe 最常见
        if (h[0] == 'M' && h[1] == 'Z') return ".exe";

        // PK\x03\x04：zip（含 apk / jar / docx / xlsx）
        if (h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4) return ".zip";

        // Rar!
        if (h[0] == 'R' && h[1] == 'a' && h[2] == 'r' && h[3] == '!') return ".rar";

        // 7z
        if (h.Length >= 6 && h[0] == 0x37 && h[1] == 0x7A && h[2] == 0xBC && h[3] == 0xAF && h[4] == 0x27 && h[5] == 0x1C)
            return ".7z";

        // %PDF
        if (h[0] == 0x25 && h[1] == 'P' && h[2] == 'D' && h[3] == 'F') return ".pdf";

        // PNG
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return ".png";

        // JPEG
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";

        // GIF
        if (h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return ".gif";

        // gzip
        if (h[0] == 0x1F && h[1] == 0x8B) return ".gz";

        // OLE2 复合文档：msi / 旧版 office
        if (h[0] == 0xD0 && h[1] == 0xCF && h[2] == 0x11 && h[3] == 0xE0) return ".msi";

        // ftyp 容器：mp4 / m4a / mov
        if (h.Length >= 12 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p') return ".mp4";

        // MP3（ID3）
        if (h.Length >= 3 && h[0] == 'I' && h[1] == 'D' && h[2] == '3') return ".mp3";

        return null;
    }

    private static string? ExtFromContentType(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        var t = mime.ToLowerInvariant().Split(';')[0].Trim();
        switch (t)
        {
            case "application/x-msdownload":
            case "application/x-dosexec":
            case "application/exe":
            case "application/x-winexe":
            case "application/vnd.microsoft.portable-executable":
                return ".exe";
            case "application/x-msi":
            case "application/x-windows-installer":
                return ".msi";
            case "application/x-zip-compressed":
            case "application/zip":
                return ".zip";
            case "application/x-rar-compressed":
            case "application/vnd.rar":
                return ".rar";
            case "application/x-7z-compressed":
                return ".7z";
            case "application/x-tar":
                return ".tar";
            case "application/gzip":
            case "application/x-gzip":
                return ".gz";
            case "application/pdf":
                return ".pdf";
            case "image/jpeg":
                return ".jpg";
            case "image/png":
                return ".png";
            case "image/gif":
                return ".gif";
            case "image/webp":
                return ".webp";
            case "image/bmp":
                return ".bmp";
            case "video/mp4":
                return ".mp4";
            case "video/x-msvideo":
                return ".avi";
            case "video/x-matroska":
                return ".mkv";
            case "audio/mpeg":
                return ".mp3";
            case "audio/mp4":
                return ".m4a";
            case "text/plain":
                return ".txt";
            case "text/html":
                return ".html";
            case "application/json":
                return ".json";
            case "application/xml":
                return ".xml";
            default:
                // octet-stream 说明服务器也不知是什么，交给文件头魔数判断，别乱给 .bin
                return null;
        }
    }

    /// <summary>名字是不是一串看不懂的 hash / 签名 / 兜底名</summary>
    private static bool LooksLikeJunk(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        if (CountReplacement(n) > 0) return true;                 // 含解码失败的乱码字符
        if (n.StartsWith("下载_")) return true;                    // 我们自己给的兜底名
        if (!Path.HasExtension(n) && n.Length >= 24) return true;  // 无扩展名的超长串
        return false;
    }

    /// <summary>把乱码串截短，避免文件名长得离谱</summary>
    private static string CleanJunk(string name)
    {
        var n = (name ?? "").Trim();
        if (n.StartsWith("下载_")) return n;
        if (n.Length > 16) n = n.Substring(0, 16);
        return n;
    }

    private static int CountReplacement(string s)
    {
        int n = 0;
        foreach (var c in s) if (c == '\uFFFD') n++;
        return n;
    }

    private static bool ContainsPercent(string s) => s.IndexOf('%') >= 0;

    private static string SafeUnescape(string s)
    {
        if (!ContainsPercent(s)) return s;
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }

    private static byte[] PercentToBytes(string s)
    {
        var list = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                list.Add((byte)((Uri.FromHex(s[i + 1]) << 4) | Uri.FromHex(s[i + 2])));
                i += 2;
            }
            else
            {
                list.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
        }
        return list.ToArray();
    }

    private static string Sanitize(string name)
    {
        name = (name ?? "").Trim().Trim('"');
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    public static string NameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            // Uri.LocalPath 已经解码过一次，这里绝不能再 UnescapeDataString，否则二次解码变乱码
            var name = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (name.Length > 120) name = name.Substring(0, 120);
            return Sanitize(name);
        }
        catch { return ""; }
    }
}

/// <summary>限速流：按每秒字节数限制读取速度</summary>
public class ThrottleStream : Stream
{
    private readonly Stream _inner;
    private readonly long _bytesPerSecond;
    private long _transferred;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public ThrottleStream(Stream inner, long bytesPerSecond)
    {
        _inner = inner;
        _bytesPerSecond = Math.Max(1, bytesPerSecond);
    }

    private void Wait(int count)
    {
        var expectedMs = (double)(_transferred + count) / _bytesPerSecond * 1000.0;
        var elapsed = _sw.Elapsed.TotalMilliseconds;
        if (expectedMs > elapsed)
            Thread.Sleep((int)Math.Min(expectedMs - elapsed, 1000));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Wait(count);
        var read = _inner.Read(buffer, offset, count);
        _transferred += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Wait(count);
        var read = await _inner.ReadAsync(buffer, offset, count, ct);
        _transferred += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        Wait(buffer.Length);
        var read = await _inner.ReadAsync(buffer, ct);
        _transferred += read;
        return read;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
