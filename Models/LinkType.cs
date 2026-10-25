namespace BLDownload.Models;

/// <summary>链接解析结果类型</summary>
public enum LinkKind { Bvid, Aid, BangumiEp, BangumiSs, Unknown }

/// <summary>解析后的链接</summary>
public record ParsedLink(LinkKind Kind, string Id, int Page = 1);
