using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Accessibility;
using CaretHUD.Interop;

namespace CaretHUD.Services;

/// <summary>
/// MSAA 取光标：AccessibleObjectFromWindow(hwnd, OBJID_CARET) + IAccessible.accLocation。
/// Chromium / Electron（Chrome、Edge、VS Code、微信、QQ）的插入点只能通过这条路线拿到。
///
/// 实测（本机 Edge，预热后）：单次 ~1ms，其中目标进程 CPU ~0.6ms；冷启动首调用 ~16ms（开启无障碍）。
/// 因此按 100ms 缓存 + 失败退避（250ms→2s）调用，失败不重试同一窗口。
/// </summary>
public static class MsaaCaretLocator
{
    private const uint ObjidCaret = 0xFFFFFFF8;
    /// <summary>成功结果的缓存时长；打字突发时由检测线程调小（默认 120ms，突发 40ms）。</summary>
    public static volatile int CacheMs = 120;
    private const int KeepOnFailureMs = 600;
    private const int FirstRetryMs = 250;
    private const int MaxRetryMs = 2000;

    private static readonly Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static readonly object Gate = new();

    private static IntPtr _ownerHwnd;
    private static IntPtr _workingHwnd;
    private static Rect _cacheCaret;
    private static long _cacheAt;
    private static bool _cacheValid;
    private static long _nextAttemptAt;
    private static bool _forceRefresh;
    private static int _failStreak;
    private static bool _loggedCold;

    /// <summary>光标/焦点变化时调用：下一次查询不吃缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _forceRefresh = true;
        }
    }

    /// <summary>真实发出的跨进程调用次数（统计用）。</summary>
    public static int CallCount;

    /// <summary>其中没有拿到插入点的次数（统计用）。</summary>
    public static int FailCount;

    /// <summary>最近一次查询"有光标对象但焦点不可编辑"：此时不必再走 UIA 兜底。</summary>
    public static bool NonEditableFocus;

    /// <summary>当前窗口连续失败次数：达到一定值才允许启用 UIA 兜底。</summary>
    public static int FailStreak => _failStreak;

    /// <summary>尝试取窗口的插入点（屏幕物理像素）。失败时按窗口退避，不会每个 tick 都重试。</summary>
    public static bool TryGetCaret(IntPtr topHwnd, IntPtr focusHwnd, out Rect caret)
    {
        caret = default;
        if (topHwnd == IntPtr.Zero)
            return false;

        lock (Gate)
        {
            var now = Environment.TickCount64;

            if (topHwnd != _ownerHwnd)
            {
                _ownerHwnd = topHwnd;
                _workingHwnd = IntPtr.Zero;
                _cacheValid = false;
                _failStreak = 0;
                _nextAttemptAt = 0;
            }

            if (_cacheValid && now - _cacheAt < CacheMs)
            {
                caret = _cacheCaret;
                return true;
            }

            if (now < _nextAttemptAt)
            {
                // 刚失败过：短暂沿用上次结果，避免浮窗闪烁
                if (_cacheValid && now - _cacheAt < KeepOnFailureMs)
                {
                    caret = _cacheCaret;
                    return true;
                }
                return false;
            }

            var sw = Stopwatch.StartNew();
            CallCount++;
            var ok = TryQuery(topHwnd, out var rect, out var hr);

            // 顶层窗口拿不到时再试焦点窗口（两者不同才有意义）
            if (!ok && focusHwnd != IntPtr.Zero && focusHwnd != topHwnd)
                ok = TryQuery(focusHwnd, out rect, out hr);

            var elapsedUs = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

            if (ok)
            {
                _workingHwnd = topHwnd;
                _failStreak = 0;
                _nextAttemptAt = now + CacheMs;
                _cacheCaret = rect;
                _cacheAt = now;
                _cacheValid = true;
                caret = rect;

                if (AppLog.Enabled && (!_loggedCold || elapsedUs > 8000))
                {
                    _loggedCold = true;
                    AppLog.Write($"msaa: caret={rect.X},{rect.Y} {rect.Width:0}x{rect.Height:0} took={elapsedUs}us cold={!_loggedCold}");
                }
                return true;
            }

            _failStreak = Math.Min(_failStreak + 1, 8);
            FailCount++;
            var backoff = Math.Min(FirstRetryMs << Math.Min(_failStreak - 1, 4), MaxRetryMs);
            _nextAttemptAt = now + backoff;

            if (AppLog.Enabled && _failStreak <= 3)
                AppLog.Write($"msaa: no caret hr=0x{hr:X8} class={ClassName(topHwnd)} took={elapsedUs}us backoff={backoff}ms");

            if (_cacheValid && now - _cacheAt < KeepOnFailureMs)
            {
                caret = _cacheCaret;
                return true;
            }
            return false;
        }
    }

    private static IntPtr _editHwnd;
    private static Rect _editCaret;
    private static long _editAt;
    private static bool _editOk;

    /// <summary>
    /// Chromium 在没有编辑框时也会返回一个"光标"对象（实测 state 含 STATE_SYSTEM_INVISIBLE，
    /// 且焦点元素是只读文档）。这里向 OBJID_CLIENT 的焦点元素要 state：只读就不算可编辑。
    /// 判定结果按 (窗口, 光标位置) 缓存 1 秒，避免在高负载页面上反复付 20~30ms 的代价。
    /// </summary>
    private static bool FocusIsEditable(IntPtr hwnd, Rect caret)
    {
        var now = Environment.TickCount64;
        if (hwnd == _editHwnd && caret == _editCaret && now - _editAt < 1000)
            return _editOk;

        _editHwnd = hwnd;
        _editCaret = caret;
        _editAt = now;
        _editOk = true;   // 默认放行：判定失败时不误伤正常的光标

        IntPtr pacc = IntPtr.Zero;
        try
        {
            var iid = IidAccessible;
            if (NativeMethods.AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref iid, out pacc) != 0 || pacc == IntPtr.Zero)
                return _editOk;

            var acc = (IAccessible)Marshal.GetObjectForIUnknown(pacc);
            var focus = acc.accFocus;
            const int StateSystemReadOnly = 0x00000040;
            int state;
            if (focus is IAccessible fa)
            {
                state = Convert.ToInt32(fa.get_accState(0));
                _editOk = (state & StateSystemReadOnly) == 0;
            }
            else if (focus is int child)
            {
                // accFocus 可能只返回子 ID：这时要用父对象的 get_accState(childId)
                state = Convert.ToInt32(acc.get_accState(child));
                _editOk = (state & StateSystemReadOnly) == 0;
            }
            else
            {
                state = -1;
                _editOk = true;
            }
            NonEditableFocus = !_editOk;
            if (AppLog.Enabled)
                AppLog.Write($"msaa: focus={focus?.GetType().Name} child={(focus is int c ? c : -1)} state=0x{state:X} editable={_editOk}");
        }
        catch
        {
            // 判定失败：放行
        }
        finally
        {
            if (pacc != IntPtr.Zero)
                Marshal.Release(pacc);
        }
        return _editOk;
    }

    private static bool TryQuery(IntPtr hwnd, out Rect caret, out int hr)
    {
        caret = default;
        hr = 0;
        IntPtr pacc = IntPtr.Zero;
        try
        {
            var iid = IidAccessible;
            hr = NativeMethods.AccessibleObjectFromWindow(hwnd, ObjidCaret, ref iid, out pacc);
            if (hr != 0 || pacc == IntPtr.Zero)
                return false;

            var acc = (IAccessible)Marshal.GetObjectForIUnknown(pacc);
            acc.accLocation(out var x, out var y, out var cx, out var cy, 0);

            // Chromium 的 caret 宽度经常是 0（真实插入点是一个竖线）。宽度为 0 不是失败。
            if (cy <= 0 || x <= -32000 || y <= -32000)
                return false;

            var rect = new Rect(x, y, Math.Max(cx, 1), cy);
            if (!FocusIsEditable(hwnd, rect))
                return false;
            caret = rect;
            return true;
        }
        catch (Exception ex)
        {
            if (AppLog.Enabled)
                AppLog.Write($"msaa: exception {ex.GetType().Name}: {ex.Message}");
            hr = ex.HResult;
            return false;
        }
        finally
        {
            if (pacc != IntPtr.Zero)
                Marshal.Release(pacc);
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "0";
        var sb = new System.Text.StringBuilder(128);
        return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }
}






