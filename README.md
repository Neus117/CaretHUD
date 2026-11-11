# CaretHUD

在输入光标旁显示当前输入状态的小浮窗，避免打字前不知道中英文或大小写状态。

## 显示什么

| 状态 | 浮窗内容 |
|---|---|
| 中文输入法 | 蓝色「中」 |
| 英文输入模式 | 灰白「英」 |
| CapsLock 开启 | 琥珀「A」 |
| NumLock 关闭 | 红色数字键盘标记 |

浮窗在光标正上方、水平居中，随光标移动；靠近屏幕边缘时自动避让（顶部放不下转下方，右侧放不下转左侧）。

## 怎么取到光标

| 场景 | 方式 |
|---|---|
| 经典输入框（记事本等） | `GetGUIThreadInfo` |
| Chrome / Edge / Electron 应用 | UI Automation（TextPattern） |
| QQ（ProseMirror 编辑器） | UI Automation |
| Word 文档 | UI Automation（TextPattern2） |

只对可编辑控件显示：浏览器页面里点击空白、选中文字不会触发。

## 构建

需要 .NET 10 SDK（Windows）。

```powershell
dotnet build
dotnet run
```

## 使用

运行后托盘出现图标，右键菜单：

| 菜单项 | 作用 |
|---|---|
| 说明 | 程序用途与支持范围 |
| 释放内存 | 手动回收工作集 |
| 退出 | 关闭程序 |

调试日志（可选）：

```powershell
$env:CARETHUD_LOG = 'C:\temp\carethud.log'
```

## 边界

- 常驻内存百 MB 级，超过 150MB 自动回收工作集。
- UI Automation 查询是跨进程调用，频率越高越可能影响 Chromium 系应用的输入；查询按 500ms（光标移动时）/ 1500ms（静止）限频，优先保证输入流畅。
- 不显示：管理员权限窗口、UWP 应用、全屏独占应用。
- 多显示器跨 DPI 未实测。
- 图标：`icon.ico`（16–256 多尺寸），同时用于托盘和 exe。

## 状态

开发中。
