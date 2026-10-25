using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BLDownload.Models;
using BLDownload.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BLDownload.ViewModels;

public partial class PageVM : ObservableObject
{
    public PageItem Item { get; init; } = null!;
    [ObservableProperty] private bool _isSelected = true;
    public string Display => Item.Display;
}

public partial class DownloadTaskVM : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "排队中";
}

public partial class MainViewModel : ObservableObject
{
    private readonly HttpClient _http;
    private readonly BiliApi _api;
    private DownloadOrchestrator _orchestrator;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private VideoInfo? _currentInfo;

    [ObservableProperty] private string _linkInput = "";
    [ObservableProperty] private string _videoTitle = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private QualityOption? _selectedQuality;
    [ObservableProperty] private string _downloadFolder;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _selectedOutputFormat = "视频 (mp4)";

    public string[] OutputFormats { get; } = { "视频 (mp4)", "MP3", "FLAC", "OPUS", "WAV" };
    private string? AudioFmtArg => SelectedOutputFormat == "视频 (mp4)" ? null : SelectedOutputFormat.ToLowerInvariant();

    public ObservableCollection<PageVM> Pages { get; } = new();
    public ObservableCollection<QualityOption> Qualities { get; } = new();
    public ObservableCollection<DownloadTaskVM> Tasks { get; } = new();

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        _downloadFolder = _settings.DownloadFolder;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        ApplyCookieHeader(_settings.Cookie);
        _api = new BiliApi(_http);
        _orchestrator = BuildOrchestrator();
        _ = CheckSavedCookieAsync();
    }

    private DownloadOrchestrator BuildOrchestrator() =>
        new(_api, new DownloadService(UserAgent, _settings.Cookie), _settings);

    private void ApplyCookieHeader(string cookie)
    {
        _http.DefaultRequestHeaders.Remove("Cookie");
        if (!string.IsNullOrEmpty(cookie))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
    }

    /// <summary>启动时验证已保存 Cookie 是否仍有效</summary>
    private async Task CheckSavedCookieAsync()
    {
        if (string.IsNullOrEmpty(_settings.Cookie)) return;
        var name = await QrLoginService.VerifyAsync(_http, _settings.Cookie);
        if (name != null) { IsLoggedIn = true; UserName = name; }
        else { StatusText = "已保存的登录态已失效，请重新登录"; }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        var win = new LoginWindow { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || string.IsNullOrEmpty(win.Cookie)) return;
        _settings.Cookie = win.Cookie;
        SettingsService.Save(_settings);
        ApplyCookieHeader(win.Cookie);
        _orchestrator = BuildOrchestrator();   // 让下载通道带上登录态
        var name = await QrLoginService.VerifyAsync(_http, win.Cookie) ?? "已登录";
        IsLoggedIn = true; UserName = name;
        StatusText = $"登录成功：{name}";
        // 已解析过视频的话重新解析，刷新可用清晰度（解锁 1080P+）
        if (HasResult && !string.IsNullOrWhiteSpace(LinkInput)) await ParseAsync();
    }

    [RelayCommand]
    private void Logout()
    {
        _settings.Cookie = "";
        SettingsService.Save(_settings);
        ApplyCookieHeader("");
        _orchestrator = BuildOrchestrator();
        IsLoggedIn = false; UserName = "";
        StatusText = "已退出登录";
        if (HasResult && !string.IsNullOrWhiteSpace(LinkInput)) _ = ParseAsync();
    }

    partial void OnDownloadFolderChanged(string value)
    {
        _settings.DownloadFolder = value;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private async Task ParseAsync()
    {
        if (string.IsNullOrWhiteSpace(LinkInput)) { StatusText = "请输入链接"; return; }
        IsBusy = true; StatusText = "解析中…"; HasResult = false;
        Pages.Clear(); Qualities.Clear(); VideoTitle = ""; SelectedQuality = null;
        try
        {
            var link = await LinkParser.ParseAsync(LinkInput, _http);
            _currentInfo = link.Kind switch
            {
                LinkKind.Bvid => await _api.GetVideoInfoAsync(link.Id),
                LinkKind.Aid => await _api.GetVideoInfoAsync("", long.Parse(link.Id)),
                LinkKind.BangumiEp => await _api.GetBangumiInfoAsync(link.Id, ""),
                LinkKind.BangumiSs => await _api.GetBangumiInfoAsync("", link.Id),
                _ => throw new InvalidOperationException("无法识别的链接，请粘贴 B 站视频/番剧链接或 BV/av 号")
            };

            if (_currentInfo.Pages.Count == 0)
                throw new InvalidOperationException("未解析到可下载的分P");

            VideoTitle = _currentInfo.Title;
            foreach (var p in _currentInfo.Pages)
                Pages.Add(new PageVM { Item = p, IsSelected = p.Page == link.Page });

            // 用第一个分P探测当前账号(免登录)可用的清晰度。
            // 注意：support_formats 会列出名义档位，但免登录时实际只下发低档 DASH 流，
            // 因此必须与真实流 id 取交集，避免用户选到下载不到的档位。
            StatusText = "获取清晰度列表…";
            var play = await _orchestrator.FetchPlayAsync(_currentInfo, _currentInfo.Pages[0]);
            var availableIds = play.Videos.Select(v => v.Id)
                .Concat(play.DurlSegments.Count > 0 ? new[] { 32, 16 } : Array.Empty<int>())
                .ToHashSet();
            foreach (var q in play.Qualities.OrderByDescending(q => q.Id))
                if (availableIds.Contains(q.Id)) Qualities.Add(q);
            if (Qualities.Count == 0)
                Qualities.Add(new QualityOption { Id = availableIds.FirstOrDefault(), Label = "默认" });
            SelectedQuality = Qualities[0];
            HasResult = true;
            StatusText = $"解析完成：{_currentInfo.Pages.Count} 个分P";
        }
        catch (Exception ex)
        {
            StatusText = "解析失败: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SelectAllPages() { foreach (var p in Pages) p.IsSelected = true; }

    [RelayCommand]
    private void DeselectAllPages() { foreach (var p in Pages) p.IsSelected = false; }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_currentInfo == null || SelectedQuality == null) { StatusText = "请先解析链接"; return; }
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) { StatusText = "请勾选要下载的分P"; return; }

        IsDownloading = true;
        _cts = new CancellationTokenSource();
        Tasks.Clear();
        var vms = selected.Select(p => new DownloadTaskVM { Name = p.Display }).ToList();
        foreach (var vm in vms) Tasks.Add(vm);

        int doneCount = 0;
        var sem = new SemaphoreSlim(Math.Max(1, _settings.MaxParallelPages));
        var jobs = selected.Zip(vms).Select(pair => Task.Run(async () =>
        {
            var (pageVM, vm) = pair;
            await sem.WaitAsync(_cts.Token);
            try
            {
                vm.Status = "下载中";
                var prog = new Progress<(double pct, string msg)>(x =>
                {
                    vm.Progress = x.pct;
                    vm.Status = x.msg;
                    // 总进度实时汇总：已完成任务按 1、进行中按各自进度加权
                    OverallProgress = vms.Sum(t => t.Status == "已完成" ? 1.0 : t.Progress) / vms.Count;
                });
                await _orchestrator.DownloadPageAsync(_currentInfo, pageVM.Item, SelectedQuality, prog, _cts.Token, AudioFmtArg);
                vm.Progress = 1; vm.Status = "已完成";
            }
            catch (OperationCanceledException) { vm.Status = "已取消"; }
            catch (Exception ex) { vm.Status = "失败: " + Truncate(ex.Message); }
            finally
            {
                int d = Interlocked.Increment(ref doneCount);
                OverallProgress = (double)d / vms.Count;
                sem.Release();
            }
        }));

        StatusText = $"开始下载 {vms.Count} 个分P…";
        await Task.WhenAll(jobs);
        StatusText = $"下载结束：{vms.Count(x => x.Status == "已完成")} 成功 / {vms.Count} 总计";
        IsDownloading = false;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>自定义保存位置（.NET 8+ WPF 自带 OpenFolderDialog）</summary>
    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存位置",
            InitialDirectory = Directory.Exists(DownloadFolder) ? DownloadFolder : null,
        };
        if (dlg.ShowDialog() == true)
            DownloadFolder = dlg.FolderName;   // OnDownloadFolderChanged 自动持久化
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(DownloadFolder);
        System.Diagnostics.Process.Start("explorer.exe", DownloadFolder);
    }

    private static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
