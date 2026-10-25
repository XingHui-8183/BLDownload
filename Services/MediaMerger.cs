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

        // ogg/opus 容器不支持视频流，封面无法以 attached_pic 流写入，
        // 需按 FLAC PICTURE 块规范构造 base64 元数据，经 ffmetadata 文件传给 ffmpeg
        if (embedCover && fmt == "opus")
        {
            string metaFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ffmeta");
            try
            {
                var meta = new System.Text.StringBuilder(";FFMETADATA1\n");
                meta.Append("METADATA_BLOCK_PICTURE=").Append(BuildFlacPictureTag(coverPath!)).Append('\n');
                if (!string.IsNullOrWhiteSpace(title)) meta.Append("title=").Append(SanitizeMeta(title!)).Append('\n');
                if (!string.IsNullOrWhiteSpace(artist)) meta.Append("artist=").Append(SanitizeMeta(artist!)).Append('\n');
                File.WriteAllText(metaFile, meta.ToString(), new System.Text.UTF8Encoding(false));
                var opusArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-y",
                    "-i", inputPath, "-f", "ffmetadata", "-i", metaFile, "-map", "0:a:0", "-map_metadata", "1" };
                opusArgs.AddRange(codec);
                opusArgs.Add(outputPath);
                await RunFfmpegAsync(opusArgs);
            }
            finally { try { File.Delete(metaFile); } catch { } }
            return;
        }

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

    /// <summary>构造 FLAC PICTURE 元数据块（type=3 封面）并 base64，供 ogg/opus 的 METADATA_BLOCK_PICTURE 使用</summary>
    private static string BuildFlacPictureTag(string coverPath)
    {
        byte[] img = File.ReadAllBytes(coverPath);
        string mime =
            img.Length > 3 && img[0] == 0xFF && img[1] == 0xD8 ? "image/jpeg" :
            img.Length > 8 && img[0] == 0x89 && img[1] == 0x50 && img[2] == 0x4E && img[3] == 0x47 ? "image/png" :
            img.Length > 15 && img[8] == 0x57 && img[9] == 0x45 && img[10] == 0x42 && img[11] == 0x50 ? "image/webp" :
            "image/jpeg";
        byte[] mimeB = System.Text.Encoding.ASCII.GetBytes(mime);
        using var ms = new MemoryStream();
        void Be(int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); ms.Write(b); }
        Be(3); Be(mimeB.Length); ms.Write(mimeB);
        Be(0); Be(0); Be(0); Be(0); Be(0);   // 描述/宽高/色深/颜色数：0 表示未知，播放器自行解析
        Be(img.Length); ms.Write(img);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string SanitizeMeta(string v) => v.Replace('\r', ' ').Replace('\n', ' ');

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
