using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BLDownload.Services;
using QRCoder;

namespace BLDownload;

/// <summary>扫码登录窗口：生成二维码 → 2s 轮询 → 成功后 DialogResult=true 并暴露 Cookie。</summary>
public partial class LoginWindow : Window
{
    private readonly HttpClient _http;
    private readonly QrLoginService _svc;
    private readonly DispatcherTimer _timer;
    private string _key = "";

    /// <summary>登录成功后的完整 Cookie 串（SESSDATA=...; bili_jct=...; ...）</summary>
    public string Cookie { get; private set; } = "";

    public LoginWindow()
    {
        InitializeComponent();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ViewModels.MainViewModel.UserAgent);
        _svc = new QrLoginService(_http);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Poll_Tick;
        Loaded += async (_, _) => await StartQrAsync();
        Closed += (_, _) => _timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
        {
            int on = 1;
            if (DwmSetWindowAttribute(src.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(src.Handle, 19, ref on, sizeof(int));
        }
    }

    private async Task StartQrAsync()
    {
        QrMask.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在生成二维码…";
        try
        {
            var s = await _svc.GenerateAsync(CancellationToken.None);
            _key = s.QrcodeKey;
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(s.Url, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 });
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            QrImage.Source = img;
            StatusText.Text = "打开 B 站手机 App → 扫一扫";
            _timer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + ex.Message;
        }
    }

    private async void Poll_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        try
        {
            var r = await _svc.PollAsync(_key, CancellationToken.None);
            switch (r.Code)
            {
                case 0:
                    Cookie = r.Cookie;
                    DialogResult = true;
                    Close();
                    return;
                case 86090:
                    StatusText.Text = "已扫码，请在手机上点击确认";
                    break;
                case 86038:
                    QrMask.Visibility = Visibility.Visible;
                    StatusText.Text = "二维码已过期，点击刷新";
                    return;   // 不重启轮询，等用户刷新
                default:
                    StatusText.Text = "等待扫码…";
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "网络异常：" + ex.Message + "（自动重试）";
        }
        _timer.Start();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await StartQrAsync();

    private void CookieToggle_Click(object sender, RoutedEventArgs e)
        => CookiePanel.Visibility = CookiePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private async void CookieOk_Click(object sender, RoutedEventArgs e)
    {
        var cookie = CookieBox.Text.Trim();
        if (cookie.Length == 0) { StatusText.Text = "请先粘贴 Cookie"; return; }
        StatusText.Text = "验证 Cookie 中…";
        var name = await QrLoginService.VerifyAsync(_http, cookie);
        if (name == null) { StatusText.Text = "Cookie 无效或已过期，请重新获取"; return; }
        Cookie = cookie;
        DialogResult = true;
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
