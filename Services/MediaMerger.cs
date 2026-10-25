using System.Diagnostics;
using System.IO;

namespace BLDownload.Services;

/// <summary>用系统 ffmpeg 将 DASH 视频流 + 音频流无损封装为 mp4</summary>
public static class MediaMerger
{
    /// <summary>提取/转码音频：fmt = mp3 / flac / opus / wav；coverPath 非空时把封面嵌入为专辑封面（wav 容器不支持则跳过）</summary>
    public static async Task ConvertAudioAsync(string inputPath, string outputPath, string fmt,
        string? coverPath = null, string? title = null, string? artist = null)
    {
        string[] codec = fmt switch
        {
            "mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "0" },
            "flac" => new[] { "-c:a", "flac" },
            "opus" => new[] { "-c:a", "libopus", "-b:a", "192k" },
            "wav" => new[] { "-c:a", "pcm_s16le" },
            _ => throw new ArgumentException("不支持的音频格式: " + fmt),
        };
        bool embedCover = coverPath != null && fmt != "wav" && File.Exists(coverPath);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath };
        if (embedCover)
            args.AddRange(new[] { "-i", coverPath!, "-map", "0:a:0", "-map", "1:v:0",
                "-c:v", "mjpeg", "-disposition:v:0", "attached_pic" });
        else
            args.Add("-vn");
        args.AddRange(codec);
        if (fmt == "mp3") args.AddRange(new[] { "-id3v2_version", "3" });   // APIC 封面依赖 id3v2
        if (!string.IsNullOrWhiteSpace(title)) args.AddRange(new[] { "-metadata", "title=" + title });
        if (!string.IsNullOrWhiteSpace(artist)) args.AddRange(new[] { "-metadata", "artist=" + artist });
        args.AddRange(new[] { "-map_metadata", "0", outputPath });
        await RunFfmpegAsync(args);
    }

    public static async Task MergeAsync(string videoPath, string audioPath, string outputPath) =>
        await RunFfmpegAsync(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", videoPath, "-i", audioPath,
            "-c", "copy", "-movflags", "+faststart", outputPath
        });

    private static async Task RunFfmpegAsync(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 启动失败，请确认已安装并加入 PATH");
        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg 处理失败: {err}");
    }

    public static bool FfmpegAvailable()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 };
        }
        catch { return false; }
    }
}
