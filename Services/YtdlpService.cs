using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace BLDownload.Services;

/// <summary>
/// yt-dlp 兜底通道：自有 API 解析/下载失败时降级使用。
/// 优先使用随程序目录的 tools\yt-dlp.exe，其次 PATH；都没有时可自动从 GitHub 下载。
/// </summary>
public class YtdlpService
{
    private static readonly string ToolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
    private static readonly string LocalExe = Path.Combine(ToolsDir, "yt-dlp.exe");

    public static string? FindExe()
    {
        if (File.Exists(LocalExe)) return LocalExe;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>下载 yt-dlp.exe：GitHub 直连 + 国内加速镜像依次尝试</summary>
    public static async Task<string> EnsureExeAsync(IProgress<string>? status, CancellationToken ct)
    {
        var found = FindExe();
        if (found != null) return found;

        status?.Report("正在下载 yt-dlp 兜底组件…");
        Directory.CreateDirectory(ToolsDir);
        const string gh = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        var candidates = new[]
        {
            "https://ghfast.top/" + gh,
            "https://gh-proxy.com/" + gh,
            gh,
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BLDownload");
        Exception? last = null;
        foreach (var url in candidates)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url, ct);
                if (bytes.Length < 1_000_000) continue; // 镜像可能返回错误页
                await File.WriteAllBytesAsync(LocalExe, bytes, ct);
                return LocalExe;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException($"yt-dlp 自动下载失败（可手动放置 tools\\yt-dlp.exe）: {last?.Message}");
    }

    /// <summary>用 yt-dlp 下载指定链接，height 限制清晰度上限。解析其进度输出。</summary>
    public static async Task DownloadAsync(string exe, string pageUrl, int maxHeight,
        string folder, string fileTitle, IProgress<double> progress, CancellationToken ct)
    {
        string format = maxHeight > 0
            ? $"bv*[height<={maxHeight}]+ba/b[height<={maxHeight}]/bv*+ba/b"
            : "bv*+ba/b";
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (var a in new[]
        {
            "--no-playlist", "--newline",
            "-f", format,
            "--merge-output-format", "mp4",
            "-o", Path.Combine(folder, $"{fileTitle}.%(ext)s"),
            pageUrl
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp 启动失败");
        var pctRegex = new Regex(@"\[download\]\s+([\d.]+)%");
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
        {
            var m = pctRegex.Match(line);
            if (m.Success) progress.Report(double.Parse(m.Groups[1].Value) / 100.0);
        }
        var err = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp 失败(exit={proc.ExitCode}): {err[..Math.Min(200, err.Length)]}");
        progress.Report(1.0);
    }
}
