using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CaretHUD.Interop;
using CaretHUD.Models;

namespace CaretHUD.Services;

/// <summary>
/// 前台窗口与插入点的检测器。全部检测在专用后台线程完成，UI 线程只接收"最新状态"。
///
/// 结构：
/// - 用 WinEvent 钩子（前台切换 / 焦点变化 / 光标移动）拿事件，事件到了立刻检测一次；
/// - 定时唤醒只做轻量兜底（Chromium 系 120ms，有光标 150ms，其它 500ms）；
/// - 昂贵的跨进程查询各自带缓存与退避：MSAA 100ms 缓存 + 失败退避，UIA 只在 Word 上异步取。
/// 实测：Chromium 单次 MSAA ~1ms（目标进程 CPU ~0.6ms），UIA 单次 20~75ms —— 所以 UIA 不允许出现在轮询里。
/// </summary>
public sealed class StatusWatcher : IDisposable
{
    private const int IdlePollMs = 500;
    private const int CaretPollMs = 200;
    private const int ChromiumPollMs = 150;
    private const int WordPollMs = 60;           // Word 只读原生 caret（µs 级），可以问得更勤
    private const int WordMissPollMs = 15;       // Word 的光标只短暂暴露（一两百毫秒），没光标时问得更密才抓得住

    // 打字突发（最近 500ms 内有任何键盘/鼠标输入）时加速采样，把"跟手延迟"压到一个屏幕帧量级
    private const int BurstWindowMs = 500;
    private const int BurstPollMs = 40;
    private const int BurstChromiumPollMs = 40;
    private const int MsaaCacheIdleMs = 120;
    private const int MsaaCacheBurstMs = 25;
    private const int MinDetectGapMs = 8;
    private const int ImeCacheMs = 50;           // 缓存比轮询短：每次检测都读最新的，消除"相位差"造成的忽快忽慢
    private const int HideGraceMs = 800;         // 光标短暂读不到时先别隐藏，避免闪一下
    private const int HideGraceWordMs = 8000;    // Word 会把系统光标销毁好几秒；保持更久，免得"消失又出现"
    private const int StatsIntervalMs = 10_000;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    // Chromium 系没有原生 caret，必须靠 MSAA 取；这类窗口按 120ms 兜底轮询
    private static readonly HashSet<string> ChromiumClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_1",
        "Chrome_WidgetWin_0",
        "MozillaWindowClass",
        "ApplicationFrameWindow",
        "OpusApp",
    };

    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dispatcher _dispatcher;
    private readonly Action<InputStatus, Rect?> _onUpdate;
    private readonly IntPtr _ownHudHandle;

    private NativeMethods.WinEventProc? _hookProc;
    private IntPtr _hookForeground;
    private IntPtr _hookFocus;
    private IntPtr _hookLocation;

    private volatile bool _dirty;
    private long _lastDetectAt;
    private long _nextPollAt;

    // 类名缓存
    private readonly StringBuilder _classBuffer = new(256);
    private IntPtr _classHwnd;
    private string _className = string.Empty;

    // IME 缓存（SendMessageTimeout 跨进程，按 150ms 降频）
    private ImeReadResult? _imeCache;
    private long _imeCacheAt;
    private IntPtr _imeCacheForeground;
    private IntPtr _imeCacheFocus;
    private InputStatus _lastValidIme = InputStatus.Empty;
    private bool _hasValidIme;
    private IntPtr _lastValidImeForeground;

    // 上一次发布的状态
    private bool _lastVisible;
    private InputStatus _lastStatus = InputStatus.Empty;
    private Rect? _lastCaret;

    // 统计（仅在开启日志时输出）
    private long _lastCaretAt;
    private IntPtr _lastCaretForeground;
    private uint _lastCaretPid;

    private long _statAt;
    private int _statTicks;
    private long _statDetectUsSum;
    private long _statDetectUsMax;
    private int _statDetect;
    private int _statSlow5;
    private int _statSlow20;
    private int _statPublishes;

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

    public void Start()
    {
        UiaCaretLocator.Start();
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _wake.Set();
        if (_thread.Join(1200))
        {
            _stop.Dispose();
            _wake.Dispose();
        }
    }

    private void Run()
    {
        _hookProc = OnWinEvent;
        _hookForeground = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _hookFocus = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_FOCUS, NativeMethods.EVENT_OBJECT_FOCUS,
            IntPtr.Zero, _hookProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _hookLocation = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _hookProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        AppLog.Write(
            $"watcher started hooks={(_hookForeground != IntPtr.Zero ? 1 : 0)}{(_hookFocus != IntPtr.Zero ? 1 : 0)}{(_hookLocation != IntPtr.Zero ? 1 : 0)}");

        var handles = new[]
        {
            _stop.SafeWaitHandle.DangerousGetHandle(),
            _wake.SafeWaitHandle.DangerousGetHandle(),
        };

        DrainMessages();
        _nextPollAt = Environment.TickCount64;
        _statAt = _nextPollAt;

        try
        {
            while (true)
            {
                var now = Environment.TickCount64;
                // 有事件待处理时只等到合并窗口结束；否则等到下一次兜底轮询
                var waitMs = _dirty
                    ? Math.Clamp(_lastDetectAt + MinDetectGapMs - now, 1, IdlePollMs)
                    : Math.Clamp(_nextPollAt - now, 1, IdlePollMs);

                var r = NativeMethods.MsgWaitForMultipleObjects(
                    2, handles, false, (uint)waitMs, NativeMethods.QS_ALLINPUT);

                if (r == NativeMethods.WAIT_OBJECT_0)
                    break;
                if (r == NativeMethods.WAIT_OBJECT_0 + 2)
                    DrainMessages();
                else if (r == NativeMethods.WAIT_OBJECT_0 + 1)
                    _wake.Reset();

                now = Environment.TickCount64;
                var due = now >= _nextPollAt;
                if (!_dirty && !due)
                    continue;
                if (now - _lastDetectAt < MinDetectGapMs)
                    continue;

                _dirty = false;
                _lastDetectAt = now;
                var idleMs = unchecked((uint)(Environment.TickCount - LastInputTick()));
                var bursting = idleMs < BurstWindowMs;
                MsaaCaretLocator.CacheMs = bursting ? MsaaCacheBurstMs : MsaaCacheIdleMs;
                DetectAndPublish(now, bursting);
                EmitStatsIfDue(now);
            }
        }
        finally
        {
            if (_hookLocation != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookLocation);
            if (_hookFocus != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookFocus);
            if (_hookForeground != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookForeground);
        }
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 只有"光标移动"这一类位置变化值得立即检测；窗口移动也发 LOCATIONCHANGE，按 idObject 过滤
        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE && idObject != NativeMethods.OBJID_CARET)
            return;

        _dirty = true;
        _wake.Set();
    }

    private static void DrainMessages()
    {
        while (NativeMethods.PeekMessage(
                   out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private static int GraceFor(string cls) =>
        cls.Equals("OpusApp", StringComparison.OrdinalIgnoreCase) ? HideGraceWordMs : HideGraceMs;
    private static uint PidOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static uint LastInputTick()
    {
        var lii = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };
        return NativeMethods.GetLastInputInfo(ref lii) ? lii.dwTime : 0;
    }

    private void DetectAndPublish(long now, bool bursting)
    {
        _statTicks++;
        var sw = Stopwatch.StartNew();

        var (visible, status, caret, cls, foreground) = Detect();
        var us = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

        // 光标短暂读不到（IME 上屏瞬间、焦点切换瞬间、Word 的弹出层）时先沿用上一次的位置，
        // 否则浮窗会闪一下再消失——Word 里尤其明显。判定按"同一个进程"而不是同一个窗口：
        // Word 的状态栏/提示层是另一个 hwnd，但仍属于同一个 Word 进程。
        if (visible)
        {
            _lastCaretAt = now;
            _lastCaretForeground = foreground;
            _lastCaretPid = PidOf(foreground);
        }
        else if (_lastCaretPid != 0 && PidOf(foreground) == _lastCaretPid && _lastCaret is not null &&
                 now - _lastCaretAt < GraceFor(cls))
        {
            visible = true;
            caret = _lastCaret;
            status = _lastStatus;
        }
        _statDetect++;
        _statDetectUsSum += us;
        if (us > _statDetectUsMax) _statDetectUsMax = us;
        if (us > 5000) _statSlow5++;
        if (us > 20000) _statSlow20++;

        _nextPollAt = now + PollInterval(cls, visible, bursting);

        if (visible == _lastVisible && status == _lastStatus && caret == _lastCaret)
            return;

        _lastVisible = visible;
        _lastStatus = status;
        _lastCaret = caret;
        _statPublishes++;

        if (AppLog.Enabled)
        {
            AppLog.Write(
                $"visible={visible} caret={(caret is { } c ? $"{c.Left:0},{c.Top:0},{c.Width:0}x{c.Height:0}" : "none")} " +
                $"cn={status.IsChinese} caps={status.CapsLock} num={status.NumLock} cls={cls} detect={us}us");
        }

        Publish(status, visible ? caret : null);
    }

    private void EmitStatsIfDue(long now)
    {
        if (!AppLog.Enabled || now - _statAt < StatsIntervalMs)
            return;

        _statAt = now;
        var avg = _statDetect > 0 ? _statDetectUsSum / _statDetect : 0;
        AppLog.Write(
            $"stat: ticks={_statTicks} detect={_statDetect} avg={avg}us max={_statDetectUsMax}us " +
            $">5ms={_statSlow5} >20ms={_statSlow20} msaa={MsaaCaretLocator.CallCount} " +
            $"msaa_fail={MsaaCaretLocator.FailCount} ime={_imeReads} publishes={_statPublishes}");
        _statTicks = 0;
        _statDetect = 0;
        _statDetectUsSum = 0;
        _statDetectUsMax = 0;
        _statSlow5 = 0;
        _statSlow20 = 0;
        _statPublishes = 0;
        _imeReads = 0;
    }

    private static int PollInterval(string cls, bool caretVisible, bool bursting)
    {
        if (cls.Equals("OpusApp", StringComparison.OrdinalIgnoreCase))
        {
            // Word 的光标经常被它自己销毁，且暴露窗口只有一两百毫秒：
            // 看不到时 15ms 抓，看到后 60ms 跟随（突发时 40ms）。
            if (!caretVisible)
                return WordMissPollMs;
            return bursting ? BurstPollMs : WordPollMs;
        }
        if (ChromiumClasses.Contains(cls))
            return bursting ? BurstChromiumPollMs : ChromiumPollMs;
        if (bursting)
            return BurstPollMs;
        return caretVisible ? CaretPollMs : IdlePollMs;
    }

    private (bool Visible, InputStatus Status, Rect? Caret, string Class, IntPtr Foreground) Detect()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _ownHudHandle)
            return (false, InputStatus.Empty, null, string.Empty, foreground);

        var cls = GetClassName(foreground);
        if (cls.Length == 0 || IgnoredClasses.Contains(cls))
            return (false, InputStatus.Empty, null, cls, foreground);

        // 1) 原生 caret：本地内核调用，µs 级
        if (CaretLocator.TryGetCaret(foreground, out var focusWindow, out var caret))
            return (true, GetImeStatus(foreground, focusWindow, cls), caret, cls, foreground);

        // Word（OpusApp）：只用原生 caret。对它调 MSAA/UIA（WM_GETOBJECT）会让 Word 常驻开启无障碍层，
        // 而 Word 的无障碍实现极重——用户实测"开着这个程序就打字卡到不能用，关掉就流畅"。
        // 原生路径在 Word 里可用（实测 caret=731,695 1x46，单次 21~150µs），代价是偶尔读不到时浮窗不显示，
        // 这对"Word 能用"是值得的交换。
        if (cls.Equals("OpusApp", StringComparison.OrdinalIgnoreCase))
            return (false, InputStatus.Empty, null, cls, foreground);

        // 2) MSAA：Chromium / Electron（本地缓存 + 退避，单次 ~1ms）
        if (MsaaCaretLocator.TryGetCaret(foreground, focusWindow, out var msaaCaret) && IsOnScreen(msaaCaret))
            return (true, GetImeStatus(foreground, focusWindow, cls), msaaCaret, cls, foreground);

        // 3) UIA：Chromium 只在 MSAA 连续失败后兜底（异步取最近结果，绝不阻塞本线程）
        if (ChromiumClasses.Contains(cls) && MsaaCaretLocator.FailStreak >= 3 &&
            UiaCaretLocator.TryGetCaret(foreground, out var uiaCaret) &&
            IsOnScreen(uiaCaret))
        {
            return (true, GetImeStatus(foreground, focusWindow, cls), uiaCaret, cls, foreground);
        }

        return (false, InputStatus.Empty, null, cls, foreground);
    }

    private int _imeReads;

    private InputStatus GetImeStatus(IntPtr foreground, IntPtr focusWindow, string cls)
    {
        var now = Environment.TickCount64;


        ImeReadResult result;
        if (_imeCache is { } cached &&
            _imeCacheForeground == foreground && _imeCacheFocus == focusWindow &&
            now - _imeCacheAt < ImeCacheMs)
        {
            result = cached;
        }
        else
        {
            result = ImeStateReader.Read(foreground, focusWindow);
            _imeReads++;
            if (result.SmtoOk)
            {
                _lastValidIme = new InputStatus(result.Chinese, false, true, 0);
                _hasValidIme = true;
                _lastValidImeForeground = foreground;
            }
            _imeCache = result;
            _imeCacheAt = now;
            _imeCacheForeground = foreground;
            _imeCacheFocus = focusWindow;
        }

        // 查询超时（目标 UI 线程忙）时沿用同一窗口上一次有效状态，避免中英误判
        var chinese = result.SmtoOk
            ? result.Chinese
            : (_hasValidIme && _lastValidImeForeground == foreground && _lastValidIme.IsChinese);

        // 锁键状态只能用 GetKeyState 的低位（toggle 状态）；GetAsyncKeyState 的低位是"自上次调用以来是否按过"，
        // 在轮询里几乎永远读到 0（实测 NumLock 开着也返回 0，小键盘标记一直是"已禁用"）。
        // 本线程现在有消息泵（MsgWaitForMultipleObjects + PeekMessage），锁键状态会及时刷新。
        var caps = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
        var num = (NativeMethods.GetKeyState(NativeMethods.VK_NUMLOCK) & 1) != 0;

        return new InputStatus(chinese, caps, num, 0);
    }

    /// <summary>把最新状态推给 UI 线程；若 UI 处理不过来，中间更新会被跳过。</summary>
    private void Publish(InputStatus status, Rect? caret)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () => _onUpdate(status, caret));
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


















