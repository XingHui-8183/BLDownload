using System.IO;
using System.Text.Json;

namespace BLDownload.Services;

/// <summary>应用设置（%AppData%\BLDownload\settings.json）。Cookie 字段预留登录态。</summary>
public class AppSettings
{
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BLDownload");
    public string Cookie { get; set; } = "";          // 预留：登录后粘贴/扫码获得的完整 Cookie 串
    public bool UseYtdlpFallback { get; set; } = true; // API 失败时自动降级 yt-dlp
    public int MaxParallelPages { get; set; } = 2;     // 同时下载的分P数
}

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BLDownload");
    private static readonly string SettingsFilePath = Path.Combine(Dir, "settings.json");
    // 改名（BiliDownloader→BLDownload）前的旧目录，用于迁移登录态与偏好
    private static readonly string LegacyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BiliDownloader");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath) && File.Exists(Path.Combine(LegacyDir, "settings.json")))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(Path.Combine(LegacyDir, "settings.json"), SettingsFilePath);
            }
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }
}
