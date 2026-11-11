using System.Drawing;
using System.Windows.Forms;
using Action = System.Action;

namespace CaretHUD.UI;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action<bool> onPauseChanged, Action<bool> onFastFollowChanged, Action<bool> onDisableUiaChanged, Action onTrimMemory)
    {
        var menu = new ContextMenuStrip();

        var pause = new ToolStripMenuItem("暂停显示") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => onPauseChanged(pause.Checked);
        menu.Items.Add(pause);

        var fast = new ToolStripMenuItem("快速跟随（可能增加输入延迟）") { CheckOnClick = true };
        fast.CheckedChanged += (_, _) => onFastFollowChanged(fast.Checked);
        menu.Items.Add(fast);

        var nativeOnly = new ToolStripMenuItem("仅原生输入框（禁用 UIA，兼容模式）") { CheckOnClick = true };
        nativeOnly.CheckedChanged += (_, _) => onDisableUiaChanged(nativeOnly.Checked);
        menu.Items.Add(nativeOnly);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即清理内存", null, (_, _) => onTrimMemory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "CaretHUD 输入状态浮窗",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                    return icon;
            }
        }
        catch
        {
            // 提取失败时退回系统图标
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
