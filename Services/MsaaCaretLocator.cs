using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Accessibility;
using CaretHUD.Interop;

namespace CaretHUD.Services;

/// <summary>
/// MSAA（Microsoft Active Accessibility）取光标位置：
/// AccessibleObjectFromWindow(hwnd, OBJID_CARET) + IAccessible.accLocation。
/// 对 Chromium/Electron 系（QQ、微信、浏览器）等原生 caret 不可用、
/// UIA 焦点不稳定的窗口有效——ImTip 的 winex.accObject.getCaret 即此实现。
/// </summary>
public static class MsaaCaretLocator
{
    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private const uint OBJID_CARET = 0xFFFFFFF8;
    private const int CallTimeoutMs = 1000;
    private const int CacheMs = 100;

    private static readonly object Gate = new();
    private static IntPtr _cacheHwnd;
    private static Rect _cacheCaret;
    private static long _cacheAt;

    public static bool TryGetCaret(IntPtr hwnd, out Rect caret)
    {
        caret = default;
        if (hwnd == IntPtr.Zero)
            return false;

        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (hwnd == _cacheHwnd && now - _cacheAt < CacheMs)
            {
                caret = _cacheCaret;
                return true;
            }

            // 个别应用的 MSAA 调用可能挂起，放到任务里加超时保护
            var task = Task.Run(() => TryGetCaretCore(hwnd));
            if (!task.Wait(CallTimeoutMs))
                return false;

            var (ok, rc) = task.Result;
            if (!ok)
                return false;

            _cacheHwnd = hwnd;
            _cacheCaret = rc;
            _cacheAt = now;
            caret = rc;
            return true;
        }
    }

    private static (bool Ok, Rect Caret) TryGetCaretCore(IntPtr hwnd)
    {
        IntPtr pacc = IntPtr.Zero;
        try
        {
            var iid = IID_IAccessible;
            if (NativeMethods.AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref iid, out pacc) != 0 || pacc == IntPtr.Zero)
                return (false, default);

            var acc = (IAccessible)Marshal.GetObjectForIUnknown(pacc);
            acc.accLocation(out var x, out var y, out var cx, out var cy, 0);
            if (cy <= 0 || cx <= 0)
                return (false, default);

            return (true, new Rect(x, y, cx, cy));
        }
        catch
        {
            return (false, default);
        }
        finally
        {
            if (pacc != IntPtr.Zero)
                Marshal.Release(pacc);
        }
    }
}
