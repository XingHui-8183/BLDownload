namespace BLDownload.Models;

/// <summary>一个可下载的视频分 P / 番剧集</summary>
public class PageItem
{
    public long Cid { get; init; }   // 新视频 cid 可超 Int32 范围
    public int Page { get; init; }
    public string Part { get; init; } = "";
    public int DurationSeconds { get; init; }
    public string Bvid { get; init; } = "";
    public int EpId { get; init; }       // 番剧专用
    public string Display => $"P{Page} {Part} ({Format(DurationSeconds)})";
    public string DomainUrl => EpId > 0
        ? $"https://www.bilibili.com/bangumi/play/ep{EpId}"
        : $"https://www.bilibili.com/video/{Bvid}?p={Page}";
    public static string Format(int s) => $"{s / 3600:D2}:{s % 3600 / 60:D2}:{s % 60:D2}";
}

/// <summary>视频元信息（UGC 或 番剧统一模型）</summary>
public class VideoInfo
{
    public string Title { get; init; } = "";
    public string Bvid { get; init; } = "";
    public long Aid { get; init; }
    public bool IsBangumi { get; init; }
    public string CoverUrl { get; init; } = "";     // 封面图（音频嵌入封面用）
    public string OwnerName { get; init; } = "";    // UP 主 / 制作方（写 ID3 artist）
    public List<PageItem> Pages { get; init; } = new();
}

/// <summary>清晰度选项</summary>
public class QualityOption
{
    public int Id { get; init; }          // B 站 quality code: 6/16/32/64/74/80/116/112/125/126/127
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>一条 DASH 流（视频或音频）</summary>
public class StreamEntry
{
    public int Id { get; init; }
    public string Url { get; init; } = "";
    public List<string> BackupUrls { get; init; } = new();
    public string Codecs { get; init; } = "";
    public long Bandwidth { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
}

/// <summary>playurl 接口解析结果</summary>
public class PlayInfo
{
    public List<QualityOption> Qualities { get; init; } = new();
    public List<StreamEntry> Videos { get; init; } = new();
    public List<StreamEntry> Audios { get; init; } = new();
    public List<string> DurlSegments { get; init; } = new();   // 传统 mp4 回退
    public string Message { get; set; } = "";                  // 接口提示（如登录限制）
}
