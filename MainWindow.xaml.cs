using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BLDownload;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Win11 深色标题栏，与 Fluent 深色内容区统一</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
        {
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 2004+/Win11）；19 为早期 Insider 构建
            if (DwmSetWindowAttribute(src.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(src.Handle, 19, ref on, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
