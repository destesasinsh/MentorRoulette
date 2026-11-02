namespace MentorRoulette;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// 排队相关的原生接口封装。
/// ContentsFinder.QueueInfo 提供 QueueRoulette / QueueDuties / CancelQueue，
/// 不用去点任务搜索器界面。
/// </summary>
internal static unsafe class QueueController
{
    internal static bool InQueue => Conditions.Instance() != null && Conditions.Instance()->InDutyQueue;

    internal static byte QueuedRouletteId
    {
        get
        {
            ContentsFinder* finder = ContentsFinder.Instance();
            if (finder == null)
                return 0;

            var info = finder->GetQueueInfo();
            return info == null ? (byte)0 : info->QueuedContentRouletteId;
        }
    }

    /// <summary>排随机任务（导师随机 = 9）。</summary>
    internal static void QueueRoulette(uint rouletteId)
    {
        ContentsFinder* finder = ContentsFinder.Instance();
        if (finder == null)
            return;

        Service.Log.Info($"[Queue] QueueRoulette({rouletteId})");
        finder->QueueInfo.QueueRoulette((byte)rouletteId);
    }

    /// <summary>排指定副本（ContentFinderCondition id）。</summary>
    internal static void QueueDuty(uint cfcId)
    {
        ContentsFinder* finder = ContentsFinder.Instance();
        if (finder == null || cfcId == 0)
            return;

        uint* entries = stackalloc uint[1];
        entries[0] = cfcId;

        Service.Log.Info($"[Queue] QueueDuties({cfcId})");
        finder->QueueInfo.QueueDuties(entries, 1);
    }

    internal static void CancelQueue()
    {
        ContentsFinder* finder = ContentsFinder.Instance();
        if (finder == null)
            return;

        Service.Log.Info("[Queue] CancelQueue()");
        finder->QueueInfo.CancelQueue();
    }

    /// <summary>确认匹配弹出框。</summary>
    internal static bool AcceptPop()
    {
        if (!AddonHelper.TryGetReady("ContentsFinderConfirm", out AtkUnitBase* confirm))
            return false;

        AddonHelper.Fire(confirm, 8);
        return true;
    }

    internal static int PartySize => Service.PartyList.Length;

    internal static bool IsPartyLeader => Service.PartyList.Length > 0 &&
                                          Service.Objects.LocalPlayer != null &&
                                          Service.PartyList[0].EntityId == Service.Objects.LocalPlayer.EntityId;

    internal static string[] PartyNames()
    {
        int len = Service.PartyList.Length;
        if (len == 0)
            return [];

        string[] names = new string[len];
        for (int i = 0; i < len; i++)
            names[i] = Service.PartyList[i].Name.TextValue;

        return names;
    }
}
