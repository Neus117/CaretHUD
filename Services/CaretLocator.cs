using System.Windows;
using CaretHUD.Interop;

namespace CaretHUD.Services;

/// <summary>通过 GetGUIThreadInfo 定位前台焦点控件的插入点。</summary>
public static class CaretLocator
{
    /// <summary>
    /// 尝试获取前台窗口当前焦点控件的插入点（屏幕物理像素坐标）。
    /// 返回 false 表示当前没有可见的文本插入点。
    /// </summary>
    public static bool TryGetCaret(IntPtr foreground, out IntPtr focusWindow, out Rect caretScreen)
    {
        focusWindow = IntPtr.Zero;
        caretScreen = default;

        if (foreground == IntPtr.Zero)
            return false;

        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0)
            return false;

        var gti = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };

        if (!NativeMethods.GetGUIThreadInfo(threadId, ref gti))
            return false;

        focusWindow = gti.hwndFocus;

        // 没有 caret 窗口或宽高均为 0：无文本插入点
        if (gti.hwndCaret == IntPtr.Zero)
            return false;
        if (gti.rcCaret.Width == 0 && gti.rcCaret.Height == 0)
            return false;

        var pt = new NativeMethods.POINT
        {
            X = gti.rcCaret.Left,
            Y = gti.rcCaret.Bottom,
        };

        if (!NativeMethods.ClientToScreen(gti.hwndCaret, ref pt))
            return false;

        // 部分应用（如记事本滚动/最小化时）会返回极端坐标，过滤掉
        var vsLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vsTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vsWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vsHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        const int slack = 16;
        if (pt.X < vsLeft - slack || pt.X > vsLeft + vsWidth + slack ||
            pt.Y < vsTop - slack || pt.Y > vsTop + vsHeight + slack)
        {
            return false;
        }

        caretScreen = new Rect(
            pt.X,
            pt.Y - gti.rcCaret.Height,
            Math.Max(gti.rcCaret.Width, 1),
            Math.Max(gti.rcCaret.Height, 1));

        return true;
    }
}
