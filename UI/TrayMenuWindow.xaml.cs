using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CaretHUD.Interop;

namespace CaretHUD.UI;

/// <summary>托盘右键菜单：自绘深色菜单，外观与浮窗一致。</summary>
public partial class TrayMenuWindow : Window
{
    private readonly Action _onAbout;
    private readonly Action _onTrim;
    private readonly Action _onExit;

    private DateTime _shownAt;
    private bool _closing;

    public TrayMenuWindow(Action onAbout, Action onTrim, Action onExit)
    {
        InitializeComponent();
        _onAbout = onAbout;
        _onTrim = onTrim;
        _onExit = onExit;

        Deactivated += OnDeactivated;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseSafely(); };
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 关闭期间不再响应失活（避免关闭过程中重入 Close 抛异常）
        if (_closing)
            return;

        // Show 之后系统可能短暂夺走激活，忽略最初 300ms 内的失活，避免菜单一闪就关
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 300)
            CloseSafely();
    }

    private void CloseSafely()
    {
        if (_closing)
            return;

        _closing = true;
        Close();
    }

    public void ShowAtScreen(int screenX, int screenY)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        Left = screenX / scale;
        Top = screenY / scale;

        Show();
        Activate();
        _shownAt = DateTime.UtcNow;
        AdjustToScreen(scale);
    }

    private void AdjustToScreen(double scale)
    {
        var vsRight = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN) + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vsBottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN) + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        // 菜单贴近屏幕右侧/底部时向内收，避免超出屏幕
        if ((Left + ActualWidth) * scale > vsRight)
            Left = (vsRight - ActualWidth * scale) / scale;
        if ((Top + ActualHeight) * scale > vsBottom)
            Top = (vsBottom - ActualHeight * scale) / scale;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        CloseSafely();
        // 延迟到菜单关闭完成后再显示说明窗口，避免在窗口关闭期间创建新窗口
        Dispatcher.BeginInvoke(new Action(_onAbout), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnTrimClick(object sender, RoutedEventArgs e)
    {
        _onTrim();
        CloseSafely();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        CloseSafely();
        _onExit();
    }
}
