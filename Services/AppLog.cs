using System.IO;

namespace CaretHUD.Services;

/// <summary>统一调试日志（仅在设置 CARETHUD_LOG 环境变量时启用）。</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static TextWriter? _writer;
    private static bool _initialized;

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
            _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
