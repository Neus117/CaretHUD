using System.IO;
using System.Text;

namespace CaretHUD.Services;

/// <summary>
/// 统一日志。诊断日志只在设置 CARETHUD_LOG 环境变量时启用（写入该路径）；
/// 崩溃日志始终写到 %LOCALAPPDATA%\CaretHUD\crash.log，供用户反馈时读取。
/// 两条通道都带大小上限，避免"只增不减"。
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 8 * 1024 * 1024;

    private static readonly object Gate = new();
    private static TextWriter? _writer;
    private static bool _initialized;
    private static bool _crashInit;
    private static string? _crashPath;

    /// <summary>日志是否启用。调用处先查这个，避免无日志时构造插值字符串。</summary>
    public static bool Enabled
    {
        get
        {
            lock (Gate)
            {
                EnsureInitialized();
                return _writer is not null;
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            EnsureInitialized();
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }

    /// <summary>崩溃/未处理异常：无论是否开启诊断日志都落盘。</summary>
    public static void WriteCrash(string message)
    {
        try
        {
            lock (Gate)
            {
                EnsureCrashPath();
                if (_crashPath is null || !TryRotate(_crashPath))
                    return;
                File.AppendAllText(_crashPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志失败不影响主流程
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
            return;
        _initialized = true;

        var path = Environment.GetEnvironmentVariable("CARETHUD_LOG");
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            TryRotate(path);
            _writer = new StreamWriter(
                File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private static void EnsureCrashPath()
    {
        if (_crashInit)
            return;
        _crashInit = true;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaretHUD");
            Directory.CreateDirectory(dir);
            _crashPath = Path.Combine(dir, "crash.log");
        }
        catch
        {
            _crashPath = null;
        }
    }

    /// <summary>超过上限就把旧文件改名成 .1（只保留一份）。</summary>
    private static bool TryRotate(string path)
    {
        try
        {
            if (!File.Exists(path))
                return true;
            var info = new FileInfo(path);
            if (info.Length < MaxBytes)
                return true;
            var old = path + ".1";
            if (File.Exists(old))
                File.Delete(old);
            File.Move(path, old);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
