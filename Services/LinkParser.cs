using System.Text.RegularExpressions;
using BLDownload.Models;

namespace BLDownload.Services;

/// <summary>
/// 解析 B 站链接 / 分享暗链，支持：
/// - https://www.bilibili.com/video/BVxxxx  (含 ?p=N)
/// - av 号、纯 BV 号
/// - https://www.bilibili.com/bangumi/play/epxxx / ssxxx
/// - b23.tv 短链（需网络解析重定向，由调用方先 ResolveShortLinkAsync）
/// </summary>
public static class LinkParser
{
    public static async Task<ParsedLink> ParseAsync(string input, HttpClient http)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrEmpty(input))
            return new ParsedLink(LinkKind.Unknown, "");

        // 分享文本常混有中文标题、反引号、引号等噪声，先提取第一个 http(s) 链接
        var url = ExtractUrl(input);
        if (url != null) input = url;

        // b23.tv / 短链先跟随重定向拿到真实地址
        if (Regex.IsMatch(input, @"b23\.tv/|bili2233\.cn/"))
            input = await ResolveRedirectAsync(input, http);

        // 提取 ?p= 分P
        int page = 1;
        var pm = Regex.Match(input, @"[?&]p=(\d+)", RegexOptions.IgnoreCase);
        if (pm.Success) page = int.Parse(pm.Groups[1].Value);

        var bvid = Regex.Match(input, @"(BV[0-9A-Za-z]{10})");
        if (bvid.Success) return new ParsedLink(LinkKind.Bvid, bvid.Groups[1].Value, page);

        var aid = Regex.Match(input, @"av(\d+)", RegexOptions.IgnoreCase);
        if (aid.Success) return new ParsedLink(LinkKind.Aid, aid.Groups[1].Value, page);

        var ep = Regex.Match(input, @"ep(\d+)", RegexOptions.IgnoreCase);
        if (ep.Success) return new ParsedLink(LinkKind.BangumiEp, ep.Groups[1].Value, page);

        var ss = Regex.Match(input, @"ss(\d+)", RegexOptions.IgnoreCase);
        if (ss.Success) return new ParsedLink(LinkKind.BangumiSs, ss.Groups[1].Value, page);

        return new ParsedLink(LinkKind.Unknown, input);
    }

    /// <summary>从任意文本中提取第一个 http(s) 链接（排除空白、引号、反引号、中文标点包裹）</summary>
    private static string? ExtractUrl(string text)
    {
        var m = Regex.Match(text, @"https?://[^\s""'`<>()（）【】\[\]，。；、！？]+");
        if (!m.Success) return null;
        // 中文分享文本里 URL 后常紧跟中文标点（不在上面排除类里的），再裁一次尾部
        return m.Value.TrimEnd('.', ',', ';', '：', ':');
    }

    private static async Task<string> ResolveRedirectAsync(string url, HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return resp.RequestMessage?.RequestUri?.ToString() ?? url;
        }
        catch { return url; }
    }
}
