using System.IO;

namespace BLDownload.Services;

/// <summary>
/// 流式下载器：多连接 Range 分块并行 + 失败重试 + 备用 CDN 切换。
/// B 站 CDN 强制校验 Referer，缺失会 403（市面下载器失败的主因之一）。
/// </summary>
public class DownloadService
{
    private readonly HttpClient _http;
    private const int ChunkConnections = 6;
    private const int MaxRetryPerChunk = 3;

    /// <summary>独立 HttpClient：大文件下载不能用短 Timeout（会掐断流），设无限超时+取消令牌控制</summary>
    public DownloadService(string userAgent, string cookie)
    {
        _http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        if (!string.IsNullOrEmpty(cookie))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
    }

    /// <summary>下载单条流到文件。progress 报告 (已完成字节, 总字节)</summary>
    public async Task DownloadAsync(IEnumerable<string> urlCandidates, string destPath, string referer,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var urls = urlCandidates.ToList();
        long total = -1;
        string workingUrl = urls[0];

        // 探测可用 CDN 与总大小
        foreach (var u in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, u);
                req.Headers.Referrer = new Uri(referer);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    if (resp.Content.Headers.ContentRange?.Length is long len) { total = len; workingUrl = u; break; }
                }
                if (resp.IsSuccessStatusCode) { total = resp.Content.Headers.ContentLength ?? -1; workingUrl = u; break; }
            }
            catch (IOException) { continue; }
        }
        if (total <= 0) throw new InvalidOperationException("无法连接视频 CDN（链接可能已过期，请重新解析）");

        long done = 0;
        bool useChunks = total > 4 * 1024 * 1024;

        if (!useChunks)
        {
            await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            done = await DownloadRangeAsync(workingUrl, referer, fs, 0, total - 1, ct,
                n => Interlocked.Add(ref done, n));
            progress?.Report((done, total));
            return;
        }

        // 预分配文件，各分块用独立句柄写自己的区间
        using (var pre = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            pre.SetLength(total);

        long chunkSize = Math.Max((long)total / ChunkConnections, 1);
        var chunkTasks = new List<Task>();
        for (long s = 0; s < total; s += chunkSize)
        {
            long start = s;                                        // 显式拷贝，避免闭包捕获循环变量终值
            long end = Math.Min(start + chunkSize - 1, total - 1);
            chunkTasks.Add(Task.Run(async () =>
            {
                int attempt = 0;
                Exception? last = null;
                while (attempt++ < MaxRetryPerChunk)
                {
                    long added = 0;   // 本次尝试已计字节，失败回滚避免重试重复计数
                    try
                    {
                        await using var own = new FileStream(destPath, FileMode.Open, FileAccess.Write,
                            FileShare.ReadWrite, 1 << 18, FileOptions.Asynchronous);
                        await DownloadRangeAsync(workingUrl, referer, own, start, end, ct,
                            n => { Interlocked.Add(ref added, n); Interlocked.Add(ref done, n); });
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Add(ref done, -Interlocked.Read(ref added));
                        last = ex; await Task.Delay(1000 * attempt, ct);
                    }
                }
                throw new IOException($"分块下载失败({start}-{end}): {last?.Message}", last);
            }, ct));
        }

        var timer = new System.Timers.Timer(500);
        timer.Elapsed += (_, _) => progress?.Report((Interlocked.Read(ref done), total));
        timer.Start();
        try { await Task.WhenAll(chunkTasks); }
        finally { timer.Stop(); timer.Dispose(); }
        progress?.Report((total, total));
    }

    /// <summary>下载 [start,end] 区间写入 stream；onBytes 实时报告每段写入字节（用于进度）</summary>
    private async Task<long> DownloadRangeAsync(string url, string referer, Stream stream,
        long start, long end, CancellationToken ct, Action<int>? onBytes = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        stream.Seek(start, SeekOrigin.Begin);
        long written = 0;
        var buffer = new byte[256 * 1024];
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            onBytes?.Invoke(read);
        }
        return written;
    }

    /// <summary>把多个 mp4 分片按顺序二进制拼接（durl 传统格式可直接拼接）</summary>
    public static async Task ConcatFilesAsync(IEnumerable<string> parts, string dest)
    {
        await using var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write);
        foreach (var p in parts)
        {
            await using var inFs = new FileStream(p, FileMode.Open, FileAccess.Read);
            await inFs.CopyToAsync(outFs);
        }
    }
}
