using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BLDownload.Services;

/// <summary>
/// B 站 WBI 签名（x/player/wbi/playurl 等接口必需）。
/// 算法：nav 接口取 img_key/sub_key → 固定重排表混排 → 参数排序拼接 + wts → MD5 = w_rid
/// </summary>
public class WbiSigner
{
    private static readonly int[] ReorderTable =
    {
        46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13
    };

    private string? _mixKey;
    private DateTime _cachedAt = DateTime.MinValue;

    public async Task<string> GetMixKeyAsync(HttpClient http)
    {
        if (_mixKey != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(10))
            return _mixKey;

        var json = await http.GetStringAsync("https://api.bilibili.com/x/web-interface/nav");
        using var doc = JsonDocument.Parse(json);
        var wbi = doc.RootElement.GetProperty("data").GetProperty("wbi_img");
        string imgKey = ExtractKey(wbi.GetProperty("img_url").GetString()!);
        string subKey = ExtractKey(wbi.GetProperty("sub_url").GetString()!);
        string raw = imgKey + subKey;
        _mixKey = new string(ReorderTable.Select(i => raw[i]).ToArray());
        _cachedAt = DateTime.UtcNow;
        return _mixKey;
    }

    /// <summary>对查询参数签名，返回带 wts/w_rid 的完整 query 字符串</summary>
    public async Task<string> SignQueryAsync(HttpClient http, Dictionary<string, string> ps)
    {
        var mixKey = await GetMixKeyAsync(http);
        ps["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var query = string.Join("&", ps
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(FilterValue(kv.Value))}"));
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixKey))).ToLowerInvariant();
        ps["w_rid"] = hash;
        return string.Join("&", ps.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    // WBI 规定：值中的 ! ' ( ) * 需剔除
    private static string FilterValue(string v) => new(v.Where(c => "!'()*".IndexOf(c) < 0).ToArray());

    private static string ExtractKey(string url) =>
        Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
}
