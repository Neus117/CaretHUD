using System.Diagnostics;
using System.Drawing;
using System.Windows;
using CaretHUD.Interop;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace CaretHUD.Services;

/// <summary>
/// UI Automation 兜底，只给 Word（OpusApp）用：Word 的插入点官方建议走 UIA，GetGUIThreadInfo 在
/// Word 激活后有 1~2 秒延迟。
///
/// 实测 UIA 单次查询（FocusedElement + GetSelection + GetBoundingRectangles）会吃掉目标进程
/// 20~75ms 的 UI 线程时间，所以在检测线程上同步等它是不可接受的。这里改成：
/// 独立工作线程 + 最近一次结果缓存，检测线程从不等它；最多 2 次/秒；同一次调用挂起超过 4 秒
/// 就停 30 秒并重建自动化对象，绝不堆积 Task。
/// </summary>
public static class UiaCaretLocator
{
    private const int MinRequestMs = 1000;
    private const int FreshMs = 1200;
    private const int StuckMs = 4000;
    private const int SuspendMs = 30_000;
    private const int EstimateLineHeight = 32;

    private static readonly object Gate = new();
    private static readonly AutoResetEvent Request = new(false);
    private static Thread? _worker;
    private static IntPtr _pendingFg;
    private static IntPtr _requestFg;
    private static int _busy;
    private static long _requestAt;
    private static long _suspendUntil;
    private static IntPtr _resultFg;
    private static Rect _resultRect;
    private static long _resultAt;
    private static bool _hasResult;
    private static bool _resetAutomation;
    private static int _consecutiveMiss;
    private static UIA3Automation? _automation;

    public static void Start()
    {
        lock (Gate)
        {
            if (_worker is not null)
                return;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "CaretHUD.Uia",
            };
            _worker.Start();
        }
    }

    /// <summary>取最近一次 UIA 结果（可能落后一两百毫秒），绝不阻塞调用线程。</summary>
    public static bool TryGetCaret(IntPtr foreground, out Rect caret)
    {
        caret = default;
        lock (Gate)
        {
            var now = Environment.TickCount64;

            if (_worker is null)
                return false;

            if (_busy != 0 && now - _requestAt > StuckMs)
            {
                // 某次查询挂在目标进程里：放弃其结果，冷却一段时间，重建自动化对象
                _busy = 0;
                _resetAutomation = true;
                _suspendUntil = now + SuspendMs;
                _hasResult = false;
                if (AppLog.Enabled)
                    AppLog.Write($"uia: query stuck >{StuckMs}ms, suspended {SuspendMs / 1000}s");
            }

            // 连续拿不到光标（例如焦点在不可编辑区域）时逐步降频，避免为"看不见"的窗口反复付无障碍的税
            var interval = MinRequestMs * (1 << Math.Min(Math.Max(_consecutiveMiss - 2, 0), 3));
            if (now >= _suspendUntil && _busy == 0 &&
                (foreground != _requestFg || now - _requestAt >= interval))
            {
                _requestFg = foreground;
                _requestAt = now;
                _pendingFg = foreground;
                _busy = 1;
                Request.Set();
            }

            if (_hasResult && _resultFg == foreground && now - _resultAt < FreshMs)
            {
                caret = _resultRect;
                return true;
            }
            return false;
        }
    }

    public static void Dispose()
    {
        Request.Set();
        lock (Gate)
        {
            _automation?.Dispose();
            _automation = null;
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Request.WaitOne();
            IntPtr fg;
            lock (Gate)
            {
                fg = _pendingFg;
                if (_resetAutomation)
                {
                    _resetAutomation = false;
                    try { _automation?.Dispose(); } catch { /* 忽略 */ }
                    _automation = null;
                }
            }

            if (fg == IntPtr.Zero)
                continue;

            var sw = Stopwatch.StartNew();
            var (ok, rect) = Query(fg);
            var elapsedMs = sw.ElapsedMilliseconds;

            lock (Gate)
            {
                _busy = 0;
                if (ok)
                {
                    _resultFg = fg;
                    _resultRect = rect;
                    _resultAt = Environment.TickCount64;
                    _hasResult = true;
                }
                if (!ok) _consecutiveMiss = Math.Min(_consecutiveMiss + 1, 8);
                if (AppLog.Enabled && (!ok || elapsedMs > 150))
                    AppLog.Write($"uia: {(ok ? $"caret={rect.X},{rect.Y} {rect.Width:0}x{rect.Height:0}" : "no caret")} took={elapsedMs}ms");
            }
        }
    }

    private static UIA3Automation Automation()
    {
        if (_automation is null)
        {
            _automation = new UIA3Automation
            {
                ConnectionTimeout = TimeSpan.FromMilliseconds(500),
                TransactionTimeout = TimeSpan.FromMilliseconds(500),
            };
        }
        return _automation;
    }

    private static (bool Ok, Rect Caret) Query(IntPtr foreground)
    {
        try
        {
            var automation = Automation();
            var focused = automation.FocusedElement();
            if (focused is null)
                return (false, default);

            var className = GetClassName(foreground);
            var isWord = className.Equals("OpusApp", StringComparison.OrdinalIgnoreCase);
            var type = focused.ControlType;
            var editable = type == ControlType.Edit
                           || type == ControlType.Group
                           || (type == ControlType.Document && isWord);
            if (!editable)
                return (false, default);

            var focusedBounds = focused.BoundingRectangle;

            if (isWord)
            {
                var text2 = focused.Patterns.Text2.PatternOrDefault;
                if (text2 is not null)
                {
                    var caretRange = text2.GetCaretRange(out _);
                    if (caretRange is not null && TryRangeRect(caretRange, out var r2))
                    {
                        if (IsWholeBounds(r2, focusedBounds))
                            return (true, CaretAtTopLeft(r2));
                        if (CaretInsideBounds(r2, focusedBounds))
                            return (true, new Rect(r2.X, r2.Y, Math.Max(r2.Width, 1), r2.Height));
                    }
                }
            }

            var textPattern = focused.Patterns.Text.PatternOrDefault;
            if (textPattern is null)
                return (false, default);

            var selection = textPattern.GetSelection();
            if (selection is not null && selection.Length > 0 && TryRangeRect(selection[0], out var r1))
            {
                if (IsWholeBounds(r1, focusedBounds))
                    return (true, CaretAtTopLeft(r1));
                if (CaretInsideBounds(r1, focusedBounds))
                    return (true, new Rect(r1.X, r1.Y, Math.Max(r1.Width, 1), r1.Height));
            }

            // 选区为空：退化为文档末段右缘
            try
            {
                var docRects = textPattern.DocumentRange.GetBoundingRectangles();
                if (docRects is not null && docRects.Length > 0 && docRects[^1].Height > 0)
                {
                    var c = docRects[^1];
                    var rc = new Rectangle(c.Right - 1, c.Y, 1, c.Height);
                    if (CaretInsideBounds(rc, focusedBounds))
                        return (true, new Rect(rc.X, rc.Y, 1, rc.Height));
                }
            }
            catch
            {
                // 文档范围不可用：忽略
            }

            if (focusedBounds.Width > 0 && focusedBounds.Height > 0)
                return (true, new Rect(focusedBounds.Left + 16, focusedBounds.Top + 16, 1, EstimateLineHeight));

            return (false, default);
        }
        catch (Exception ex)
        {
            if (AppLog.Enabled)
                AppLog.Write($"uia: exception {ex.GetType().Name}: {ex.Message}");
            return (false, default);
        }
    }

    private static Rect CaretAtTopLeft(Rectangle r) => new(r.X, r.Y, 1, EstimateLineHeight);

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return string.Empty;
        var sb = new System.Text.StringBuilder(128);
        return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static bool IsWholeBounds(Rectangle r, Rectangle focusedBounds)
        => focusedBounds.Width > 0 && focusedBounds.Height > 0 &&
           r.Width >= focusedBounds.Width - 4 && r.Height >= focusedBounds.Height - 4;

    private static bool CaretInsideBounds(Rectangle r, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return true;

        const int slack = 8;
        return r.X >= bounds.Left - slack && r.X <= bounds.Right + slack &&
               r.Y >= bounds.Top - slack && r.Y <= bounds.Bottom + slack;
    }

    private static bool TryRangeRect(FlaUI.Core.ITextRange range, out Rectangle rect)
    {
        rect = default;
        try
        {
            var rects = range.GetBoundingRectangles();
            if (rects is null || rects.Length == 0)
                return false;
            var r = rects[0];
            if (r.Height <= 0)
                return false;
            rect = r;
            return true;
        }
        catch
        {
            return false;
        }
    }
}


