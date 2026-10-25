using System.IO;
using System.Text;
using System.Windows;
using BLDownload.Models;
using BLDownload.Services;

namespace BLDownload;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 自检模式：BLDownload.exe --parse <链接> [--download] [--no-fallback]
        //           BLDownload.exe --ytdlp <链接>   （单独验证兜底通道）
        // 结果写入 test_output.txt，用于无界面验证 API 通路
        if (e.Args.Length >= 2 && e.Args[0] == "--ytdlp")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                try {
                    var exe = await YtdlpService.EnsureExeAsync(null, CancellationToken.None);
                    var settings = SettingsService.Load();
                    var fileLock = new object();
                    void Log(string s) { lock (fileLock) File.AppendAllText("test_output.txt", s); }
                    var prog = new Progress<double>(x => Log($"[{x:P0}]\n"));
                    await YtdlpService.DownloadAsync(exe, e.Args[1], 480, settings.DownloadFolder, "ytdlp_test", prog, CancellationToken.None);
                    Log("YTDLP_OK " + exe);
                }
                catch (Exception ex) { File.AppendAllText("test_output.txt", "YTDLP_ERROR: " + ex.Message); }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        if (e.Args.Length >= 1 && e.Args[0] == "--login")
        {
            // 自检：生成二维码并轮询一次，验证扫码登录 API 通路（无需真扫码）
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                var sb = new StringBuilder();
                try
                {
                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(ViewModels.MainViewModel.UserAgent);
                    var svc = new QrLoginService(http);
                    var s = await svc.GenerateAsync(CancellationToken.None);
                    sb.AppendLine($"QR_URL: {s.Url}");
                    sb.AppendLine($"KEY: {s.QrcodeKey}");
                    var r = await svc.PollAsync(s.QrcodeKey, CancellationToken.None);
                    sb.AppendLine($"POLL_CODE: {r.Code} (86101=未扫码 即通路正常)");
                    var settings = SettingsService.Load();
                    var nav = await QrLoginService.VerifyAsync(http, settings.Cookie);
                    sb.AppendLine($"NAV_VERIFY: {(nav == null ? "未登录/无效" : nav)}");
                }
                catch (Exception ex) { sb.AppendLine("LOGIN_ERROR: " + ex); }
                File.WriteAllText("test_output.txt", sb.ToString());
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--parse")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                try { await RunParseTestAsync(e.Args[1], e.Args.Contains("--download"), e.Args.Contains("--no-fallback"), e.Args); }
                catch (Exception ex) { File.WriteAllText("test_output.txt", "ERROR: " + ex); }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        base.OnStartup(e);
        var win = new MainWindow { DataContext = new ViewModels.MainViewModel() };
        win.Show();
    }

    private static async Task RunParseTestAsync(string link, bool doDownload, bool noFallback, string[] args)
    {
        var sb = new StringBuilder();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ViewModels.MainViewModel.UserAgent);
        var settings = SettingsService.Load();
        if (!string.IsNullOrEmpty(settings.Cookie))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", settings.Cookie);
        var api = new BiliApi(http);
        if (noFallback) settings.UseYtdlpFallback = false;
        var orchestrator = new DownloadOrchestrator(api,
            new DownloadService(ViewModels.MainViewModel.UserAgent, settings.Cookie), settings);

        var parsed = await LinkParser.ParseAsync(link, http);
        sb.AppendLine($"解析: {parsed.Kind} Id={parsed.Id} P={parsed.Page}");

        VideoInfo info = parsed.Kind switch
        {
            LinkKind.Bvid => await api.GetVideoInfoAsync(parsed.Id),
            LinkKind.Aid => await api.GetVideoInfoAsync("", long.Parse(parsed.Id)),
            LinkKind.BangumiEp => await api.GetBangumiInfoAsync(parsed.Id, ""),
            LinkKind.BangumiSs => await api.GetBangumiInfoAsync("", parsed.Id),
            _ => throw new InvalidOperationException("无法识别链接")
        };
        sb.AppendLine($"标题: {info.Title}  分P数: {info.Pages.Count}  番剧: {info.IsBangumi}");
        sb.AppendLine($"封面: {info.CoverUrl}  UP: {info.OwnerName}");
        foreach (var p in info.Pages.Take(10))
            sb.AppendLine($"  {p.Display} cid={p.Cid} ep={p.EpId}");

        var play = await orchestrator.FetchPlayAsync(info, info.Pages[0]);
        sb.AppendLine($"清晰度({play.Qualities.Count}): " +
            string.Join(", ", play.Qualities.Select(q => $"{q.Id}:{q.Label}")));
        sb.AppendLine($"DASH视频流: {play.Videos.Count}  音频流: {play.Audios.Count}  durl分片: {play.DurlSegments.Count}");
        foreach (var v in play.Videos)
            sb.AppendLine($"  video id={v.Id} {v.Width}x{v.Height}@{v.FrameRate} {v.Codecs} bw={v.Bandwidth}");
        foreach (var a in play.Audios)
            sb.AppendLine($"  audio id={a.Id} bw={a.Bandwidth} codecs={a.Codecs}");
        if (!string.IsNullOrEmpty(play.Message)) sb.AppendLine("接口提示: " + play.Message);

        if (doDownload && play.Qualities.Count > 0)
        {
            var q = play.Qualities.OrderBy(x => x.Id).First(); // 最低档，测试快
            string? audioFmt = null;
            int ai = Array.IndexOf(args, "--audio");
            if (ai >= 0 && ai + 1 < args.Length) audioFmt = args[ai + 1].ToLowerInvariant();
            sb.AppendLine($"测试下载: {q.Label} audio={audioFmt ?? "-"} → {settings.DownloadFolder}");
            var prog = new Progress<(double, string)>(x => sb.AppendLine($"  [{x.Item1:P0}] {x.Item2}"));
            var path = await orchestrator.DownloadPageAsync(info, info.Pages[0], q, prog, CancellationToken.None, audioFmt);
            sb.AppendLine("下载完成: " + path + " 存在=" + File.Exists(path));
        }

        File.WriteAllText("test_output.txt", sb.ToString());
    }
}
