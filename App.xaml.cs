using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CaretHUD.Models;
using CaretHUD.Services;
using CaretHUD.UI;

namespace CaretHUD;

public partial class App : Application
{
    private const string MutexName = "CaretHUD.SingleInstance";

    private Mutex? _instanceMutex;
    private HudWindow? _hud;
    private TrayIcon? _tray;
    private TrayMenuWindow? _trayMenu;
    private AboutWindow? _aboutWindow;
    private StatusWatcher? _watcher;
    private InputStatus? _lastStatus;
    private bool _crashLogged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 任何未处理异常都留痕：之前是静默消失，用户拿不到任何信息
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.WriteCrash($"fatal: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.WriteCrash($"unobserved task: {args.Exception}");
            args.SetObserved();
        };

        // 单实例：已有实例在运行时直接退出，避免两个托盘/两套检测
        _instanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _hud = new HudWindow();
        _hud.Show();
        _hud.Hide();

        var hudHandle = new WindowInteropHelper(_hud).EnsureHandle();

        _watcher = new StatusWatcher(hudHandle, OnStatusUpdate);
        _tray = new TrayIcon();
        _tray.RightClick += ShowTrayMenu;
        _watcher.Start();
    }

    private void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.WriteCrash($"ui exception: {e.Exception}");
        e.Handled = true;
        _crashLogged = true;

        // 浮窗状态可疑时先藏起来，避免留下一个画错的位置
        try
        {
            _hud?.Hide();
        }
        catch
        {
            // 关闭阶段窗口可能已释放
        }
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is { IsVisible: true })
        {
            _trayMenu.Close();
            return;
        }

        var pos = System.Windows.Forms.Cursor.Position;
        _trayMenu = new TrayMenuWindow(ShowAbout, MemoryTrimmer.Trim, () => Shutdown());
        _trayMenu.ShowAtScreen(pos.X, pos.Y);
    }

    private void ShowAbout()
    {
        if (_aboutWindow is { IsVisible: true })
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Show();
    }

    private void OnStatusUpdate(InputStatus status, Rect? caret)
    {
        if (_hud is null)
            return;

        if (caret is null)
        {
            if (_hud.IsVisible)
                _hud.Hide();
            return;
        }

        // 只有状态真的变化时才改内容，纯移动不重绘
        if (_lastStatus != status)
        {
            _lastStatus = status;
            _hud.UpdateContent(status);
        }

        if (!_hud.IsVisible)
            _hud.Show();
        _hud.MoveNear(caret.Value);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 窗口由 Shutdown 统一关闭，这里只清理后台资源，避免关闭期间重复 Close 抛异常
        _watcher?.Dispose();
        UiaCaretLocator.Dispose();
        _tray?.Dispose();

        if (_crashLogged && AppLog.Enabled)
            AppLog.Write($"exit after handled exception (see crash.log)");

        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
