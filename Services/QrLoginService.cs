using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BLDownload.Services;

/// <summary>
/// B 站网页端扫码登录：generate 取二维码 → poll 轮询状态 → 成功后从 Set-Cookie 提取登录凭证。
/// 接口需带浏览器 UA 与 bilibili Referer，否则风控拒绝。
/// </summary>
public class QrLoginService
{
    public record QrSession(string Url, string QrcodeKey);
    /// <summary>0 成功 / 86101 未扫码 / 86090 已扫码待确认 / 86038 已过期</summary>
    public record PollResult(int Code, string Cookie);

    private readonly HttpClient _http;

    public QrLoginService(HttpClient http) => _http = http;

    public async Task<QrSession> GenerateAsync(CancellationToken ct)
    {
        using var req = NewRequest("https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
        using var resp = await _http.SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var data = doc.RootElement.GetProperty("data");
        return new QrSession(
            data.GetProperty("url").GetString() ?? "",
            data.GetProperty("qrcode_key").GetString() ?? "");
    }

    public async Task<PollResult> PollAsync(string qrcodeKey, CancellationToken ct)
    {
        using var req = NewRequest(
            $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={Uri.EscapeDataString(qrcodeKey)}&source=main-fe-header");
        using var resp = await _http.SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        // 轮询接口成功码在 data.code（外层 code 恒为 0）
        int code = root.GetProperty("data").GetProperty("code").GetInt32();
        if (code != 0) return new PollResult(code, "");

        // 登录凭证在响应 Set-Cookie（SESSDATA/bili_jct/DedeUserID/sid 等）
        var cookies = new List<string>();
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            foreach (var sc in setCookies)
            {
                var pair = sc.Split(';')[0].Trim();
                if (Regex.IsMatch(pair, @"^(SESSDATA|bili_jct|DedeUserID|DedeUserID__ckMd5|sid)=") && pair.EndsWith('='))
                    continue;   // 空值跳过
                cookies.Add(pair);
            }
        if (cookies.Count == 0 || !cookies.Any(c => c.StartsWith("SESSDATA=")))
            throw new InvalidOperationException("扫码成功但未取到登录 Cookie，请重试");
        return new PollResult(0, string.Join("; ", cookies));
    }

    /// <summary>用 Cookie 调 nav 验证登录态，返回用户名（失败/未登录返回 null）</summary>
    public static async Task<string?> VerifyAsync(HttpClient http, string cookie, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            req.Headers.Referrer = new Uri("https://www.bilibili.com/");
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var resp = await http.SendAsync(req, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var data = doc.RootElement.GetProperty("data");
            return data.TryGetProperty("isLogin", out var l) && l.GetBoolean()
                ? (data.TryGetProperty("uname", out var u) ? u.GetString() : "已登录")
                : null;
        }
        catch { return null; }
    }

    private static HttpRequestMessage NewRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        return req;
    }
}
