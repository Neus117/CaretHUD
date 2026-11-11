namespace CaretHUD.Models;

/// <summary>某一时刻输入焦点处的状态快照。</summary>
public readonly record struct InputStatus(
    bool IsChinese,
    bool CapsLock,
    bool NumLock,
    int LangId)
{
    public static readonly InputStatus Empty = new(false, false, true, 0x409);
}
