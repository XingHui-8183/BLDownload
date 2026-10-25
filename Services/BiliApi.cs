using System.Text.Json;
using BLDownload.Models;

namespace BLDownload.Services;

/// <summary>
/// B 站官方 Web API 客户端（免登录通道；Cookie 预留登录态）。
/// UGC: x/web-interface/view + x/player/wbi/playurl
/// 番剧: pgc/view/web/season + pgc/player/web/v2/playurl
/// 对响应结构做“递归查找”解析，B 站小幅改版时更抗挫。
/// </summary>
public class BiliApi
{
    public const string UgcReferer = "https://www.bilibili.com/";
    public const string PgcReferer = "https://www.bilibili.com/bangumi/";

    private readonly HttpClient _http;
    private readonly WbiSigner _wbi = new();

    public BiliApi(HttpClient http) => _http = http;

    // ---------- UGC 视频 ----------

    public async Task<VideoInfo> GetVideoInfoAsync(string bvid, long aid = 0)
    {
        string url = bvid.Length > 0
            ? $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}"
            : $"https://api.bilibili.com/x/web-interface/view?aid={aid}";
        using var doc = await GetJsonAsync(url, UgcReferer);
        var data = EnsureOk(doc.RootElement);

        var info = new VideoInfo
        {
            Title = data.GetProperty("title").GetString() ?? "",
            Bvid = data.TryGetProperty("bvid", out var bv) ? bv.GetString() ?? "" : "",
            Aid = data.TryGetProperty("aid", out var a) ? a.GetInt64() : 0,
            CoverUrl = NormalizeUrl(data.TryGetProperty("pic", out var pic) ? pic.GetString() ?? "" : ""),
            OwnerName = data.TryGetProperty("owner", out var owner) && owner.TryGetProperty("name", out var on)
                ? on.GetString() ?? "" : "",
        };
        if (data.TryGetProperty("pages", out var pages))
            foreach (var p in pages.EnumerateArray())
                info.Pages.Add(new PageItem
                {
                    Cid = p.GetProperty("cid").GetInt64(),
                    Page = p.GetProperty("page").GetInt32(),
                    Part = p.TryGetProperty("part", out var pt) ? pt.GetString() ?? "" : $"P{p.GetProperty("page")}",
                    DurationSeconds = p.TryGetProperty("duration", out var d) ? d.GetInt32() : 0,
                    Bvid = info.Bvid,
                });
        return info;
    }

    public async Task<PlayInfo> GetPlayUrlAsync(string bvid, long cid, int qn = 127)
    {
        var ps = new Dictionary<string, string>
        {
            ["bvid"] = bvid, ["cid"] = cid.ToString(),
            ["qn"] = qn.ToString(), ["fnval"] = "4048", ["fnver"] = "0", ["fourk"] = "1"
        };
        var query = await _wbi.SignQueryAsync(_http, ps);
        using var doc = await GetJsonAsync($"https://api.bilibili.com/x/player/wbi/playurl?{query}", UgcReferer);
        return ParsePlayResponse(doc.RootElement);
    }

    // ---------- 番剧 ----------

    public async Task<VideoInfo> GetBangumiInfoAsync(string epId, string ssId)
    {
        string url = epId.Length > 0
            ? $"https://api.bilibili.com/pgc/view/web/season?ep_id={epId}"
            : $"https://api.bilibili.com/pgc/view/web/season?season_id={ssId}";
        using var doc = await GetJsonAsync(url, PgcReferer);
        var root = doc.RootElement;
        var result = root.TryGetProperty("result", out var r) ? r : EnsureOk(root);

        var info = new VideoInfo
        {
            Title = result.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "番剧",
            IsBangumi = true,
            CoverUrl = NormalizeUrl(result.TryGetProperty("cover", out var cv) ? cv.GetString() ?? "" : ""),
        };
        if (result.TryGetProperty("episodes", out var eps))
        {
            int i = 1;
            foreach (var e in eps.EnumerateArray())
            {
                // 收录所有有 cid 的集（status=13 已上线 / 2 预览等均可播），playurl 阶段再验证可用性
                if (!e.TryGetProperty("cid", out var c) || c.GetInt64() == 0) continue;
                info.Pages.Add(new PageItem
                {
                    EpId = e.GetProperty("id").GetInt32(),
                    Cid = c.GetInt64(),
                    Page = i,
                    Part = e.TryGetProperty("long_title", out var lt) && lt.ValueKind == JsonValueKind.String && lt.GetString()!.Length > 0
                        ? lt.GetString()! : (e.TryGetProperty("title", out var tt) ? tt.GetString() ?? $"第{i}话" : $"第{i}话"),
                    DurationSeconds = e.TryGetProperty("duration", out var du) ? du.GetInt32() / 1000 : 0,
                });
                i++;
            }
        }
        return info;
    }

    public async Task<PlayInfo> GetBangumiPlayUrlAsync(int epId, int qn = 127)
    {
        var ps = new Dictionary<string, string>
        {
            ["ep_id"] = epId.ToString(), ["qn"] = qn.ToString(),
            ["fnval"] = "4048", ["fnver"] = "0", ["fourk"] = "1", ["platform"] = "web"
        };
        var query = await _wbi.SignQueryAsync(_http, ps);
        using var doc = await GetJsonAsync($"https://api.bilibili.com/pgc/player/web/v2/playurl?{query}", PgcReferer);
        return ParsePlayResponse(doc.RootElement);
    }

    // ---------- 公共 ----------

    /// <summary>拉取封面图字节（图片 CDN 一般不校验 Referer，仍带上以防变动）</summary>
    public async Task<byte[]> GetImageBytesAsync(string url, string referer, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>补全协议头：B 站 pic 字段可能返回 //i0.hdslb.com/... 或 http://</summary>
    private static string NormalizeUrl(string u) => u switch
    {
        _ when u.StartsWith("//") => "https:" + u,
        _ when u.StartsWith("http://") => "https://" + u[7..],
        _ => u
    };

    private async Task<JsonDocument> GetJsonAsync(string url, string referer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer);
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text); }
        catch (JsonException)
        {
            // 非 JSON（被风控返回 HTML）时给出可诊断错误
            throw new InvalidOperationException($"B站接口返回异常内容(可能被风控): {url[..Math.Min(80, url.Length)]}");
        }
    }

    private static JsonElement EnsureOk(JsonElement root)
    {
        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 0)
        {
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            throw new BiliApiException(code, msg);
        }
        return root.GetProperty("data");
    }

    /// <summary>递归查找 JSON 中第一个指定名称的属性（抗接口结构变动）</summary>
    private static JsonElement? FindFirst(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out var v)) return v;
            foreach (var p in root.EnumerateObject())
                if (FindFirst(p.Value, name) is { } hit) return hit;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (FindFirst(item, name) is { } hit) return hit;
        }
        return null;
    }

    private PlayInfo ParsePlayResponse(JsonElement root)
    {
        var play = new PlayInfo();
        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 0)
            play.Message = root.TryGetProperty("message", out var m) ? m.GetString() ?? $"code={code}" : $"code={code}";

        // 清晰度列表
        if (FindFirst(root, "support_formats") is JsonElement sf)
            foreach (var f in sf.EnumerateArray())
            {
                string label = f.TryGetProperty("new_description", out var nd) && nd.ValueKind == JsonValueKind.String
                    ? nd.GetString()! : (f.TryGetProperty("description", out var d0) ? d0.GetString() ?? "" : "");
                play.Qualities.Add(new QualityOption
                {
                    Id = f.GetProperty("quality").GetInt32(),
                    Label = label
                });
            }

        // DASH 流
        if (FindFirst(root, "dash") is JsonElement dash)
        {
            if (dash.TryGetProperty("video", out var vids))
                foreach (var v in vids.EnumerateArray()) play.Videos.Add(ReadStream(v));
            if (dash.TryGetProperty("audio", out var auds))
                foreach (var a in auds.EnumerateArray()) play.Audios.Add(ReadStream(a));
        }

        // 传统 mp4 分片回退
        if (play.Videos.Count == 0 && FindFirst(root, "durl") is JsonElement durl)
            foreach (var seg in durl.EnumerateArray())
                if (seg.TryGetProperty("url", out var u)) play.DurlSegments.Add(u.GetString()!);

        return play;
    }

    private static StreamEntry ReadStream(JsonElement e)
    {
        var s = new StreamEntry
        {
            Id = e.GetProperty("id").GetInt32(),
            Url = e.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "" :
                  e.TryGetProperty("base_url", out var b2) ? b2.GetString() ?? "" : "",
            Codecs = e.TryGetProperty("codecs", out var co) ? co.GetString() ?? "" : "",
            Bandwidth = e.TryGetProperty("bandwidth", out var bw) ? bw.GetInt64() : 0,
            Width = e.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            Height = e.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            FrameRate = e.TryGetProperty("frameRate", out var fr) && fr.ValueKind == JsonValueKind.String
                ? (double.TryParse(fr.GetString(), out var f) ? f : 0) : 0,
        };
        if (e.TryGetProperty("backupUrl", out var bu))
        {
            foreach (var item in bu.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) s.BackupUrls.Add(item.GetString()!);
        }
        else if (e.TryGetProperty("backup_url", out var bu2))
        {
            foreach (var item in bu2.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) s.BackupUrls.Add(item.GetString()!);
        }
        return s;
    }
}

public class BiliApiException : Exception
{
    public int Code { get; }
    public BiliApiException(int code, string msg) : base($"B站接口错误 code={code}: {msg}") => Code = code;
}
