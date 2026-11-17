using CaretHUD.Interop;

namespace CaretHUD.Services;

/// <summary>一次 IME 状态读取的原始结果（含诊断字段）。</summary>
public readonly record struct ImeReadResult(
    IntPtr Hwnd,
    IntPtr ImeWnd,
    int Opened,
    int ConvMode,
    bool SmtoOk,
    bool Chinese);

/// <summary>读取指定窗口的 IME 开关与中英状态。</summary>
public static class ImeStateReader
{
    public static ImeReadResult Read(IntPtr foreground, IntPtr focusWindow)
    {
        var hwnd = focusWindow != IntPtr.Zero ? focusWindow : foreground;

        var imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
        var opened = 0;
        var convMode = 0;
        var smtoOk = false;

        if (imeWnd != IntPtr.Zero)
        {
            var openResult = NativeMethods.SendMessageTimeoutInt(imeWnd, NativeMethods.WM_IME_CONTROL, NativeMethods.IMC_GETOPENSTATUS);
            var convResult = NativeMethods.SendMessageTimeoutInt(imeWnd, NativeMethods.WM_IME_CONTROL, NativeMethods.IMC_GETCONVERSIONMODE);
            smtoOk = openResult.Ok && convResult.Ok;
            opened = openResult.Value;
            convMode = convResult.Value;
        }

        var chinese = smtoOk && opened != 0 && (convMode & (int)NativeMethods.IME_CMODE_NATIVE) != 0;

        return new ImeReadResult(hwnd, imeWnd, opened, convMode, smtoOk, chinese);
    }
}
