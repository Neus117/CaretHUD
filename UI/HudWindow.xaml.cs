using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CaretHUD.Interop;
using CaretHUD.Models;
using CaretHUD.Services;

namespace CaretHUD.UI;

public partial class HudWindow : Window
{
    private static readonly SolidColorBrush ChineseBrush = CreateFrozen(0x6E, 0xA8, 0xFF);
    private static readonly SolidColorBrush EnglishBrush = CreateFrozen(0xE8, 0xEC, 0xF2);

    public HudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowStyles();
    }

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    public void UpdateContent(InputStatus status)
    {
        ImeText.Text = status.IsChinese ? "中" : "英";
        ImeText.Foreground = status.IsChinese ? ChineseBrush : EnglishBrush;
        CapsBadge.Visibility = status.CapsLock ? Visibility.Visible : Visibility.Collapsed;

        // 数字键盘：开启时不显示任何东西，关闭时才显示提醒标记
        NumBadge.Visibility = status.NumLock ? Visibility.Collapsed : Visibility.Visible;
    }

    public void MoveNear(Rect caretScreen)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (!NativeMethods.GetWindowRect(hwnd, out var wr))
            return;

        var w = wr.Width;
        var h = wr.Height;
        if (w <= 0 || h <= 0)
            return;

        // 窗口含 Grid Margin 的阴影留白，位置按胶囊本体计算
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var margin = 16 * scale;
        var capsuleW = Math.Max(1, w - margin * 2);
        var capsuleH = Math.Max(1, h - margin * 2);

        var vsLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vsTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vsRight = vsLeft + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vsBottom = vsTop + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        var gap = 4 * scale;

        // 目标：胶囊位于光标正上方，水平与光标居中对齐（不压光标和文字）
        var caretCenterX = caretScreen.Left + caretScreen.Width / 2;
        var capsuleLeft = caretCenterX - capsuleW / 2;
        var capsuleTop = caretScreen.Top - gap - capsuleH;

        // 水平夹紧
        if (capsuleLeft < vsLeft)
            capsuleLeft = vsLeft;
        if (capsuleLeft + capsuleW > vsRight)
            capsuleLeft = vsRight - capsuleW;

        // 垂直避让：上方放不下放光标下方，再不行夹紧
        if (capsuleTop < vsTop)
            capsuleTop = caretScreen.Bottom + gap;
        if (capsuleTop + capsuleH > vsBottom)
            capsuleTop = vsBottom - capsuleH;
        if (capsuleTop < vsTop)
            capsuleTop = vsTop;

        var winX = (int)Math.Round(capsuleLeft - margin);
        var winY = (int)Math.Round(capsuleTop - margin);

        if (AppLog.Enabled)
        {
            AppLog.Write(
                $"move caret={caretScreen.Left:0},{caretScreen.Top:0} {caretScreen.Width:0}x{caretScreen.Height:0} " +
                $"win={winX},{winY} scale={scale:0.##} capsule={capsuleW:0}x{capsuleH:0}");
        }

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            winX,
            winY,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
