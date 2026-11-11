using System.Text;
using System.Windows;
using System.Windows.Threading;
using CaretHUD.Interop;
using CaretHUD.Models;

namespace CaretHUD.Services;

/// <summary>
/// 前台窗口与插入点的轮询器。全部检测在专用后台线程完成，
/// UI 线程只接收"最新状态"并移动浮窗；UI 更新按版本合并，积压时只应用最新一条。
/// </summary>
public sealed class StatusWatcher : IDisposable
{
    private const int PollIntervalMs = 30;
    private const int ImeCacheMs = 100;
    private const int MemoryCheckTicks = 6000;      // 约 3 分钟
    private const int MemoryTrimThresholdMb = 120;  // 超过则清理（启动约 108MB，留正常波动余量）

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    // 这些窗口的输入控件不是原生 Edit，GetGUIThreadInfo 拿不到插入点或延迟很大，需要 UIA 兜底
    // （Word 官方建议用 UIA 获取插入点，GetGUIThreadInfo 在 Word 激活后有 1~2 秒延迟）
    private static readonly HashSet<string> UiaClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_1",
        "Chrome_WidgetWin_0",
        "MozillaWindowClass",
        "ApplicationFrameWindow",
        "OpusApp",
    };

    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Dispatcher _dispatcher;
    private readonly Action<InputStatus, Rect?> _onUpdate;
    private readonly IntPtr _ownHudHandle;

    private readonly object _pendingLock = new();
    private (InputStatus Status, Rect? Caret) _pending;
    private long _pendingVersion;

    // 类名缓存：前台窗口不变时避免每 tick 分配
    private readonly StringBuilder _classBuffer = new(256);
    private IntPtr _classHwnd;
    private string _className = string.Empty;

    // 标题缓存（仅用于识别桌宠等会导致 UIA 挂起的覆盖层窗口）
    private IntPtr _titleHwnd;
    private bool _isDshPet;

    // IME 状态缓存（SendMessageTimeout 较慢，按 100ms 降频）
    private ImeReadResult? _imeCache;
    private long _imeCacheAt;
    private IntPtr _imeCacheForeground;
    private bool _hasLastIme;
    private bool _lastChinese;

    public StatusWatcher(IntPtr ownHudHandle, Action<InputStatus, Rect?> onUpdate)
    {
        _ownHudHandle = ownHudHandle;
        _onUpdate = onUpdate;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CaretHUD.Watcher",
        };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop.Set();
        // 线程在超时内没退出时不要释放事件，避免它继续 WaitOne 时抛异常（进程退出时由系统回收）
        if (_thread.Join(1000))
            _stop.Dispose();
    }

    private void Run()
    {
        AppLog.Write("watcher started (background thread)");
        var lastVisible = false;
        var lastStatus = InputStatus.Empty;
        Rect? lastCaret = null;
        var tick = 0;

        while (!_stop.WaitOne(PollIntervalMs))
        {
            // 定期内存清理（防御长时间运行后缓慢上涨）
            if (++tick >= MemoryCheckTicks)
            {
                tick = 0;
                var ws = MemoryTrimmer.CurrentWorkingSetMb;
                if (ws > MemoryTrimThresholdMb)
                {
                    MemoryTrimmer.Trim();
                    if (AppLog.Enabled)
                        AppLog.Write($"memory trim: {ws}MB -> {MemoryTrimmer.CurrentWorkingSetMb}MB");
                }
            }

            var (visible, status, caret) = Detect();

            if (visible == lastVisible && status == lastStatus && caret == lastCaret)
                continue;

            lastVisible = visible;
            lastStatus = status;
            lastCaret = caret;

            if (AppLog.Enabled)
            {
                AppLog.Write(
                    $"visible={visible} caret={(caret is { } c ? $"{c.Left:0},{c.Top:0},{c.Width:0}x{c.Height:0}" : "none")} " +
                    $"cn={status.IsChinese} caps={status.CapsLock} num={status.NumLock}");
            }

            Publish(status, visible ? caret : null);
        }
    }

    /// <summary>把最新状态推给 UI 线程；若 UI 处理不过来，中间更新会被跳过。</summary>
    private void Publish(InputStatus status, Rect? caret)
    {
        long version;
        lock (_pendingLock)
        {
            _pending = (status, caret);
            version = ++_pendingVersion;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            (InputStatus Status, Rect? Caret) snapshot;
            lock (_pendingLock)
            {
                // 已经有更新的状态在排队，本条跳过
                if (_pendingVersion != version)
                    return;
                snapshot = _pending;
            }

            _onUpdate(snapshot.Status, snapshot.Caret);
        });
    }

    private (bool Visible, InputStatus Status, Rect? Caret) Detect()
    {
        var foreground = NativeMethods.GetForegroundWindow();

        var cls = GetClassName(foreground);
        if (cls.Length == 0 || foreground == _ownHudHandle || IgnoredClasses.Contains(cls))
            return (false, InputStatus.Empty, null);

        if (CaretLocator.TryGetCaret(foreground, out var focusWindow, out var caret))
        {
            var status = GetImeStatus(foreground, focusWindow);
            return (true, status, caret);
        }

        // 桌宠等覆盖层窗口的 UIA 查询会挂起，跳过（标题含 dsh-pet）
        if (UiaClasses.Contains(cls) && !IsDshPetWindow(foreground) &&
            UiaCaretLocator.TryGetCaret(foreground, out var uiaCaret) &&
            IsOnScreen(uiaCaret))
        {
            var status = GetImeStatus(foreground, IntPtr.Zero);
            return (true, status, uiaCaret);
        }

        return (false, InputStatus.Empty, null);
    }

    private bool IsDshPetWindow(IntPtr hwnd)
    {
        if (hwnd == _titleHwnd)
            return _isDshPet;

        var buffer = new StringBuilder(256);
        var title = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
        _isDshPet = title.Contains("dsh-pet", StringComparison.OrdinalIgnoreCase);
        _titleHwnd = hwnd;
        return _isDshPet;
    }

    private InputStatus GetImeStatus(IntPtr foreground, IntPtr focusWindow)
    {
        var now = Environment.TickCount64;
        var foregroundChanged = _imeCacheForeground != foreground;

        if (_imeCache is { } cached && !foregroundChanged && now - _imeCacheAt < ImeCacheMs)
            return cached.Status;

        var result = ImeStateReader.Read(foreground, focusWindow);

        // 诊断日志：记录 IME 原始返回值。前台变化或中/英跳变时输出，用于定位“无故跳变”。
        if (AppLog.Enabled && (foregroundChanged || (_hasLastIme && _lastChinese != result.Chinese)))
        {
            AppLog.Write(
                $"ime: fg=0x{foreground.ToInt64():X} cls={GetClassName(foreground)} " +
                $"hwnd=0x{result.Hwnd.ToInt64():X} ime=0x{result.ImeWnd.ToInt64():X} " +
                $"opened={result.Opened} conv={result.ConvMode} smto={result.SmtoOk} " +
                $"cn={result.Chinese} caps={result.Caps} num={result.Num} fgChanged={foregroundChanged}");
        }

        _hasLastIme = true;
        _lastChinese = result.Chinese;
        _imeCache = result;
        _imeCacheAt = now;
        _imeCacheForeground = foreground;
        return result.Status;
    }

    private string GetClassName(IntPtr hwnd)
    {
        if (hwnd == _classHwnd)
            return _className;

        _classBuffer.Clear();
        _className = hwnd != IntPtr.Zero && NativeMethods.GetClassName(hwnd, _classBuffer, _classBuffer.Capacity) > 0
            ? _classBuffer.ToString()
            : string.Empty;
        _classHwnd = hwnd;
        return _className;
    }

    private static bool IsOnScreen(Rect r)
    {
        var vsLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vsTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vsRight = vsLeft + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vsBottom = vsTop + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        const int slack = 16;
        return r.Left >= vsLeft - slack && r.Right <= vsRight + slack &&
               r.Top >= vsTop - slack && r.Bottom <= vsBottom + slack;
    }
}
