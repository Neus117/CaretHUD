using System.Drawing;
using System.Windows;
using CaretHUD.Interop;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace CaretHUD.Services;

/// <summary>
/// 对 Chromium / Electron / Firefox / Word 等 GetGUIThreadInfo 拿不到插入点的窗口，
/// 用 UI Automation TextPattern 兜底。查询放在独立任务里并设硬超时：某些窗口的 UIA
/// 调用会挂起，超时后暂停 UIA 一段时间并重建自动化对象，保证检测线程不会卡死。
/// </summary>
public static class UiaCaretLocator
{
    private const int ActiveIntervalMs = 500;
    private const int IdleIntervalMs = 1500;
    private const int KeepOnFailureMs = 800;
    private const int QueryTimeoutMs = 1500;
    private const int SuspendAfterTimeoutMs = 30000;
    private const int EstimateLineHeight = 32;

    private static readonly object Gate = new();
    private static UIA3Automation? _automation;
    private static long _lastAttempt;
    private static long _lastSuccess;
    private static long _suspendUntil;
    private static int _queryInterval = 1500;
    private static bool _hasCaret;
    private static Rect _lastCaret;
    private static IntPtr _lastForeground;

    public static bool TryGetCaret(IntPtr foreground, out Rect caret)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;

            // UIA 超时后暂停一段时间，避免对挂起的窗口反复阻塞
            if (now < _suspendUntil)
            {
                caret = _lastCaret;
                return _hasCaret;
            }

            if (foreground != _lastForeground)
            {
                _lastForeground = foreground;
                _lastAttempt = 0;
            }

            if (_lastAttempt != 0 && now - _lastAttempt < _queryInterval)
            {
                caret = _lastCaret;
                return _hasCaret;
            }

            _lastAttempt = now;

            var automation = _automation ??= new UIA3Automation
            {
                ConnectionTimeout = TimeSpan.FromMilliseconds(500),
                TransactionTimeout = TimeSpan.FromMilliseconds(500),
            };

            var task = Task.Run(() => QueryCore(automation, foreground));
            if (!task.Wait(QueryTimeoutMs))
            {
                // 查询挂起：丢弃当前自动化对象，暂停 UIA，检测线程继续走
                _automation = null;
                _suspendUntil = now + SuspendAfterTimeoutMs;
                return Fail(out caret, $"uia timeout ({QueryTimeoutMs}ms), suspended {SuspendAfterTimeoutMs / 1000}s");
            }

            var (ok, rect, source) = task.Result;
            if (ok)
            {
                if (AppLog.Enabled)
                    AppLog.Write($"uia({source}): caret={rect.X},{rect.Y} {rect.Width}x{rect.Height}");
                return Success(out caret, rect, now);
            }

            return KeepOrFail(out caret, now, source);
        }
    }

    private static (bool Ok, Rectangle Rect, string Source) QueryCore(UIA3Automation automation, IntPtr foreground)
    {
        try
        {
            var focused = automation.FocusedElement();
            if (focused is null)
                return (false, default, "no focused element");

            // 只接受可编辑控件：输入框(Edit)、contenteditable 容器(Group)，
            // 以及 Word 文档区(Document + OpusApp)。浏览器页面也是 Document，但只读，必须排除。
            var className = GetClassName(foreground);
            var isWord = className.Equals("OpusApp", StringComparison.OrdinalIgnoreCase);
            var type = focused.ControlType;
            var editable = type == ControlType.Edit
                           || type == ControlType.Group
                           || (type == ControlType.Document && isWord);
            if (!editable)
                return (false, default, $"focused not editable: {type} in {className}");

            var focusedBounds = focused.BoundingRectangle;

            // 1. TextPattern2.GetCaretRange：仅 Word 使用（Word 的 selection 不可靠）
            if (isWord)
            {
                var text2 = focused.Patterns.Text2.PatternOrDefault;
                if (text2 is not null)
                {
                    var caretRange = text2.GetCaretRange(out _);
                    if (caretRange is not null && TryRangeRect(caretRange, out var r2, out _))
                    {
                        if (IsWholeBounds(r2, focusedBounds))
                            return (true, CaretAtTopLeft(r2), "text2-whole");
                        if (CaretInsideBounds(r2, focusedBounds))
                            return (true, r2, "text2-caret");
                    }
                }
            }

            var textPattern = focused.Patterns.Text.PatternOrDefault;
            if (textPattern is null)
                return (false, default, $"focused not text: {focused.ControlType}");

            var isEmptyDoc = IsEmptyDocument(textPattern);

            var selection = textPattern.GetSelection();
            if (selection is not null && selection.Length > 0)
            {
                var range = selection[0];
                if (TryRangeRect(range, out var r1, out _))
                {
                    // 空编辑器返回的是整个输入框矩形：光标在文本起始处
                    if (IsWholeBounds(r1, focusedBounds))
                        return (true, CaretAtTopLeft(r1), "whole-bounds");

                    // 部分端（如 QQ 空编辑器）返回输入框外的元素矩形：丢弃
                    if (CaretInsideBounds(r1, focusedBounds))
                        return (true, r1, "selection");
                }
            }

            // 空文档：光标在文本起始处（左上角）
            if (isEmptyDoc)
            {
                try
                {
                    var docRects = textPattern.DocumentRange.GetBoundingRectangles();
                    if (docRects is not null && docRects.Length > 0)
                        return (true, CaretAtTopLeft(docRects[0]), "empty-doc");
                }
                catch
                {
                    // 忽略
                }
            }

            // 非空文档但选区为空：按"光标在文末"用文档最后一段的右边缘
            try
            {
                var docRects = textPattern.DocumentRange.GetBoundingRectangles();
                if (docRects is not null && docRects.Length > 0 && docRects[^1].Height > 0)
                {
                    var c = docRects[^1];
                    var rc = new Rectangle(c.Right - 1, c.Y, 1, c.Height);
                    if (CaretInsideBounds(rc, focusedBounds))
                        return (true, rc, $"doc-end({docRects.Length})");
                }
            }
            catch
            {
                // 忽略
            }

            // 最后兜底：扩展为字符
            if (selection is not null && selection.Length > 0)
            {
                try
                {
                    var clone = selection[0].Clone();
                    clone.ExpandToEnclosingUnit(TextUnit.Character);
                    var rects = clone.GetBoundingRectangles();
                    if (rects is not null && rects.Length > 0 && rects[^1].Height > 0)
                    {
                        var c = rects[^1];
                        var rc = new Rectangle(c.Right - 1, c.Y, 1, c.Height);
                        if (CaretInsideBounds(rc, focusedBounds))
                            return (true, rc, $"expand-char({rects.Length})");
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            // 所有 UIA 矩形都不可用时：用输入框自身左上角作为文本起始位置
            if (focusedBounds.Width > 0 && focusedBounds.Height > 0)
                return (true, new Rectangle(focusedBounds.Left + 16, focusedBounds.Top + 16, 1, EstimateLineHeight), "focused-bounds");

            return (false, default, "no bounding rects");
        }
        catch (Exception ex)
        {
            return (false, default, $"exception {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsEmptyDocument(FlaUI.Core.Patterns.ITextPattern textPattern)
    {
        try
        {
            return textPattern.DocumentRange.GetText(1).Length == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Rectangle CaretAtTopLeft(Rectangle r) => new(r.X, r.Y, 1, EstimateLineHeight);

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return string.Empty;
        var sb = new System.Text.StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static bool IsWholeBounds(Rectangle r, Rectangle focusedBounds)
        => focusedBounds.Width > 0 && focusedBounds.Height > 0 &&
           r.Width >= focusedBounds.Width - 4 && r.Height >= focusedBounds.Height - 4;

    private static bool CaretInsideBounds(Rectangle r, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return true; // 没有焦点元素矩形信息时不限制

        const int slack = 8;
        return r.X >= bounds.Left - slack && r.X <= bounds.Right + slack &&
               r.Y >= bounds.Top - slack && r.Y <= bounds.Bottom + slack;
    }

    private static bool TryRangeRect(FlaUI.Core.ITextRange range, out Rectangle rect, out string why)
    {
        rect = default;
        why = string.Empty;
        try
        {
            var rects = range.GetBoundingRectangles();
            if (rects is null || rects.Length == 0)
            {
                why = "empty rects";
                return false;
            }

            var r = rects[0];
            if (r.Height <= 0)
            {
                why = $"zero height {r.Width}x{r.Height}";
                return false;
            }

            rect = r;
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    private static bool Success(out Rect caret, Rectangle r, long now)
    {
        var newCaret = new Rect(r.X, r.Y, Math.Max(r.Width, 1), r.Height);
        // 光标位置变了 → 用高频；没变 → 降频
        _queryInterval = newCaret != _lastCaret ? ActiveIntervalMs : IdleIntervalMs;
        _lastCaret = newCaret;
        _hasCaret = true;
        _lastSuccess = now;
        caret = _lastCaret;
        return true;
    }

    private static bool KeepOrFail(out Rect caret, long now, string reason)
    {
        if (_hasCaret && now - _lastSuccess < KeepOnFailureMs)
        {
            // 焦点元素还在，但这次查询失败：沿用上次结果，避免闪烁
            _queryInterval = IdleIntervalMs;
            caret = _lastCaret;
            return true;
        }

        if (_hasCaret && AppLog.Enabled)
            AppLog.Write($"uia: keep expired, {reason}");

        return Fail(out caret, reason);
    }

    private static bool Fail(out Rect caret, string reason)
    {
        _hasCaret = false;
        _queryInterval = IdleIntervalMs;
        caret = default;
        if (AppLog.Enabled)
            AppLog.Write($"uia: {reason}");
        return false;
    }
}
