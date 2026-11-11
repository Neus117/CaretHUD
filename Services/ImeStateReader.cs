using CaretHUD.Interop;
using CaretHUD.Models;

namespace CaretHUD.Services;

/// <summary>读取指定窗口的 IME 状态与键盘锁定状态。</summary>
public static class ImeStateReader
{
    public static InputStatus Read(IntPtr foreground, IntPtr focusWindow)
    {
        var hwnd = focusWindow != IntPtr.Zero ? focusWindow : foreground;

        var chinese = false;
        var imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
        if (imeWnd != IntPtr.Zero)
        {
            var opened = NativeMethods.SendMessageTimeoutInt(imeWnd, NativeMethods.WM_IME_CONTROL, NativeMethods.IMC_GETOPENSTATUS);
            var convMode = NativeMethods.SendMessageTimeoutInt(imeWnd, NativeMethods.WM_IME_CONTROL, NativeMethods.IMC_GETCONVERSIONMODE);
            chinese = opened != 0 && (convMode & (int)NativeMethods.IME_CMODE_NATIVE) != 0;
        }

        var caps = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
        var num = (NativeMethods.GetKeyState(NativeMethods.VK_NUMLOCK) & 1) != 0;

        return new InputStatus(chinese, caps, num, 0);
    }
}
