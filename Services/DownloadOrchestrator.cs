using System.IO;
using BLDownload.Models;

namespace BLDownload.Services;

/// <summary>
/// 下载编排：解析 → playurl 取流 → 分块下载(视频+音频并行) → ffmpeg 合并；
/// 任一环节失败且允许兜底时，自动降级 yt-dlp。
/// </summary>
public class DownloadOrchestrator
{
    private readonly BiliApi _api;
    private readonly DownloadService _dl;
    private readonly AppSettings _settings;

    public DownloadOrchestrator(BiliApi api, DownloadService dl, AppSettings settings)
    {
        _api = api; _dl = dl; _settings = settings;
    }

    /// <summary>取某一分P的可用流信息</summary>
    public async Task<PlayInfo> FetchPlayAsync(VideoInfo info, PageItem page) =>
        page.EpId > 0 ? await _api.GetBangumiPlayUrlAsync(page.EpId)
                      : await _api.GetPlayUrlAsync(info.Bvid, page.Cid);

    /// <summary>下载一个分P，返回最终文件路径。progress: (0~1, 阶段文字)；audioFmt 非空则仅下载并转码音频(mp3/flac/opus/wav)</summary>
    public async Task<string> DownloadPageAsync(VideoInfo info, PageItem page, QualityOption quality,
        IProgress<(double, string)> progress, CancellationToken ct, string? audioFmt = null)
    {
        Directory.CreateDirectory(_settings.DownloadFolder);
        string referer = info.IsBangumi ? BiliApi.PgcReferer : BiliApi.UgcReferer;
        string baseName = SanitizeFileName($"{info.Title} P{page.Page} {page.Part} [{quality.Label}]");
        string ext = audioFmt == null ? ".mp4" : "." + audioFmt;
        string finalPath = Path.Combine(_settings.DownloadFolder, baseName + ext);
        string? coverTmp = null;   // 音频模式下的封面临时文件（catch 兜底分支也要能引用）

        try
        {
            progress.Report((0, "获取播放流…"));
            var play = await FetchPlayAsync(info, page);

            // 选流：匹配清晰度 id，同 id 取码率最高（通常是 avc/hevc/av1 中带宽最优者）
            var video = audioFmt == null
                ? play.Videos.Where(v => v.Id == quality.Id).MaxBy(v => v.Bandwidth)
                : play.Videos.MaxBy(v => v.Bandwidth);   // 音频模式不关心画质，仅判断通道
            var audio = play.Audios.MaxBy(a => a.Bandwidth);
            if (audioFmt != null && audio == null && play.DurlSegments.Count == 0)
                throw new InvalidOperationException("未取到音频流");
            if (video == null && play.DurlSegments.Count == 0 && audioFmt == null)
                throw new InvalidOperationException($"未取到可用视频流: {play.Message}");

            string tempDir = Path.Combine(_settings.DownloadFolder, "_temp");
            Directory.CreateDirectory(tempDir);
            string vTmp = Path.Combine(tempDir, baseName + "_v.m4s");
            string aTmp = Path.Combine(tempDir, baseName + "_a.m4s");

            // 音频模式：先取封面图（失败降级为无封面，不阻断下载）
            if (audioFmt != null && info.CoverUrl.Length > 0)
            {
                try
                {
                    byte[] coverBytes = await _api.GetImageBytesAsync(info.CoverUrl, referer, ct);
                    coverTmp = Path.Combine(tempDir, baseName + "_cover.img");
                    await File.WriteAllBytesAsync(coverTmp, coverBytes, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { coverTmp = null; }
            }

            if (audioFmt != null && audio != null) // ---- 纯音频模式：只下音频流再转码 ----
            {
                var aMeter = new SpeedMeter();
                var aProg = new Progress<(long done, long total)>(x =>
                {
                    aMeter.Add(x.done);
                    double p = (double)x.done / Math.Max(x.total, 1);
                    progress.Report((0.9 * p, $"下载音频流 {p:P0} · {SpeedMeter.Fmt(aMeter.Speed)}"));
                });
                await _dl.DownloadAsync(new[] { audio.Url }.Concat(audio.BackupUrls), aTmp, referer, aProg, ct);
                progress.Report((0.95, $"转码为 {audioFmt.ToUpperInvariant()}…"));
                await MediaMerger.ConvertAudioAsync(aTmp, finalPath, audioFmt, coverTmp, info.Title, info.OwnerName);
                TryDelete(aTmp); TryDelete(coverTmp);
            }
            else if (video != null) // DASH 通道
            {
                var vMeter = new SpeedMeter(); var aMeter = new SpeedMeter();
                var vProg = new Progress<(long done, long total)>(x =>
                {
                    vMeter.Add(x.done);
                    double p = (double)x.done / Math.Max(x.total, 1);
                    progress.Report(((audio == null ? 0.9 : 0.5) * p,
                        $"下载视频流 {p:P0} · {SpeedMeter.Fmt(vMeter.Speed + aMeter.Speed)}"));
                });
                var aProg = new Progress<(long done, long total)>(x =>
                {
                    aMeter.Add(x.done);
                    double p = (double)x.done / Math.Max(x.total, 1);
                    progress.Report((0.5 + 0.4 * p,
                        $"下载音频流 {p:P0} · {SpeedMeter.Fmt(vMeter.Speed + aMeter.Speed)}"));
                });

                var vTask = _dl.DownloadAsync(new[] { video.Url }.Concat(video.BackupUrls), vTmp, referer, vProg, ct);
                var aTask = audio != null
                    ? _dl.DownloadAsync(new[] { audio.Url }.Concat(audio.BackupUrls), aTmp, referer, aProg, ct)
                    : Task.CompletedTask;
                await Task.WhenAll(vTask, aTask);

                if (audio != null)
                {
                    progress.Report((0.95, "合并音视频…"));
                    await MediaMerger.MergeAsync(vTmp, aTmp, finalPath);
                    TryDelete(vTmp); TryDelete(aTmp);
                }
                else
                {
                    File.Move(vTmp, finalPath, true);
                }
            }
            else // 传统 mp4 分片通道（≤480P 常见，无需 ffmpeg）
            {
                var segPaths = new List<string>();
                var meter = new SpeedMeter();
                for (int i = 0; i < play.DurlSegments.Count; i++)
                {
                    string seg = Path.Combine(tempDir, $"{baseName}_seg{i}.mp4");
                    int idx = i;
                    var p = new Progress<(long done, long total)>(x =>
                    {
                        meter.Add(x.done);
                        double sp = (double)x.done / Math.Max(x.total, 1);
                        progress.Report((((double)idx + sp) / play.DurlSegments.Count,
                            $"下载分片 {i + 1}/{play.DurlSegments.Count} · {SpeedMeter.Fmt(meter.Speed)}"));
                    });
                    await _dl.DownloadAsync(new[] { play.DurlSegments[i] }, seg, referer, p, ct);
                    segPaths.Add(seg);
                }
                if (audioFmt != null) // durl 内嵌音频：先拼 mp4 再提取转码
                {
                    string mTmp = Path.Combine(tempDir, baseName + "_durl.mp4");
                    await DownloadService.ConcatFilesAsync(segPaths, mTmp);
                    progress.Report((0.95, $"转码为 {audioFmt.ToUpperInvariant()}…"));
                    await MediaMerger.ConvertAudioAsync(mTmp, finalPath, audioFmt, coverTmp, info.Title, info.OwnerName);
                    TryDelete(mTmp); TryDelete(coverTmp);
                }
                else await DownloadService.ConcatFilesAsync(segPaths, finalPath);
                foreach (var s in segPaths) TryDelete(s);
            }

            progress.Report((1, "完成"));
            return finalPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (_settings.UseYtdlpFallback)
        {
            // ---- yt-dlp 兜底 ----
            progress.Report((0, $"API 通道失败({Truncate(ex.Message)})，切换 yt-dlp…"));
            string exe = await YtdlpService.EnsureExeAsync(null, ct);
            int height = QualityHeight(quality.Id);
            var p = new Progress<double>(x => progress.Report((x, "yt-dlp 下载中…")));
            await YtdlpService.DownloadAsync(exe, page.DomainUrl, height, _settings.DownloadFolder, baseName, p, ct);
            if (audioFmt != null) // 兜底产物为 mp4，本地再转音频
            {
                string vPath = Path.Combine(_settings.DownloadFolder, baseName + ".mp4");
                progress.Report((0.95, $"转码为 {audioFmt.ToUpperInvariant()}…"));
                await MediaMerger.ConvertAudioAsync(vPath, finalPath, audioFmt, coverTmp, info.Title, info.OwnerName);
                TryDelete(vPath); TryDelete(coverTmp);
            }
            progress.Report((1, "完成(yt-dlp)"));
            return finalPath;
        }
    }

    public static int QualityHeight(int id) => id switch
    {
        6 => 240, 16 => 360, 32 => 480, 64 => 720, 74 => 720,
        80 => 1080, 112 => 1080, 116 => 1080, 120 => 1080,
        125 => 2160, 126 => 2160, 127 => 4320, _ => 0
    };

    /// <summary>滑动速度计：每 500ms 采样一次字节增量算瞬时速度（bytes/s）</summary>
    private sealed class SpeedMeter
    {
        private long _lastBytes;
        private DateTime _lastTime = DateTime.Now;
        public double Speed { get; private set; }

        public void Add(long doneBytes)
        {
            var now = DateTime.Now;
            double dt = (now - _lastTime).TotalSeconds;
            if (dt >= 0.5)
            {
                Speed = Math.Max(0, doneBytes - _lastBytes) / dt;
                _lastBytes = doneBytes;
                _lastTime = now;
            }
        }

        public static string Fmt(double bps) => bps <= 0 ? "-- B/s"
            : bps >= 1 << 20 ? $"{bps / 1048576:F1} MB/s"
            : $"{bps / 1024:F0} KB/s";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[..120] : name;
    }
    private static string Truncate(string s) => s.Length > 60 ? s[..60] + "…" : s;
    private static void TryDelete(string? p) { if (p != null) try { File.Delete(p); } catch { } }
}
