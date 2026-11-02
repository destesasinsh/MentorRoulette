namespace MentorRoulette;

using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// 自动确认进本。
///
/// DailyRoutines 走的是 ExecuteCommand(NotifyTerritoryReady)，国服当前的
/// FFXIVClientStructs 还没有 ExecuteCommandManager，所以这里等价地用
/// 任务确认窗口的 Commence 回调（就是界面上那个「确定」按钮）。
///
/// 每个弹窗实例最多点 3 次、间隔 1.5 秒，避免每帧刷回调。
/// </summary>
internal static class DutyConfirm
{
    private static nint     lastAddon = nint.Zero;
    private static DateTime lastFire  = DateTime.MinValue;
    private static int      fireCount;

    internal static unsafe bool TryAccept()
    {
        if (!AddonHelper.TryGetReady("ContentsFinderConfirm", out AtkUnitBase* addon))
        {
            lastAddon = nint.Zero;
            fireCount = 0;
            return false;
        }

        if (lastAddon != (nint)addon)
        {
            lastAddon = (nint)addon;
            fireCount = 0;
            lastFire  = DateTime.MinValue;
        }

        if (fireCount >= 3)
            return false;

        if ((DateTime.UtcNow - lastFire).TotalSeconds < 1.5)
            return false;

        lastFire = DateTime.UtcNow;
        fireCount++;

        AddonHelper.Fire(addon, 8);
        DiagLog.Add($"确认进本 #{fireCount}");
        Service.Log.Info($"[Confirm] commence #{fireCount}");
        return true;
    }
}
