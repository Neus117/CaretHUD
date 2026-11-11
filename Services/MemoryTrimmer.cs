using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaretHUD.Services;

/// <summary>内存清理：回收托管对象（含 COM 包装）并把工作集还给系统。</summary>
internal static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static long CurrentWorkingSetMb => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

    public static void Trim()
    {
        // 双次回收：第一次标记，finalizer 释放 COM 包装，第二次真正回收
        GC.Collect(2, GCCollectionMode.Optimized);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Optimized);

        // 把工作集还给系统（下次访问按需换入）
        EmptyWorkingSet(GetCurrentProcess());
    }
}
