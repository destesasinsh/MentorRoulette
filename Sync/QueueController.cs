namespace MentorRoulette;

using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// 排队相关封装。
///
/// 主路径照 DailyRoutines / OmenTools 的做法：按签名找到原生
/// RequestContentsFinder / RequestContentsFinderRoulette / CancelContentsFinder，
/// 直接传副本 id + ContentsFinderOption，不依赖任务搜索器界面。
/// 签名扫不到时退回 ContentsFinder.QueueInfo 的旧接口。
/// </summary>
internal static unsafe class QueueController
{
    // ---------- ContentsFinderOption ----------

    [StructLayout(LayoutKind.Explicit, Size = 10)]
    internal struct ContentsFinderOption
    {
        [FieldOffset(0)] public byte Supply;
        [FieldOffset(1)] public byte Config817to820;
        [FieldOffset(2)] public byte UnrestrictedParty;
        [FieldOffset(3)] public byte MinimalIL;
        [FieldOffset(4)] public byte LevelSync;
        [FieldOffset(5)] public byte SilenceEcho;
        [FieldOffset(6)] public byte ExplorerMode;
        [FieldOffset(7)] public byte LootRules;
        [FieldOffset(8)] public byte IsLimitedLevelingRoulette;
        [FieldOffset(9)] public byte Unknown9;

        internal static ContentsFinderOption Default => new() { Config817to820 = 1 };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool RequestDutyDelegate(uint* contentsId, uint count, uint a3, ref ContentsFinderOption option);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RequestRouletteDelegate(ushort rouletteId, ref ContentsFinderOption option);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CancelDelegate(byte a1);

    private static readonly nint RequestDutyPtr;
    private static readonly nint RequestRoulettePtr;
    private static readonly nint CancelPtr;

    internal static bool NativeAvailable => RequestDutyPtr != 0 || RequestRoulettePtr != 0;

    static QueueController()
    {
        RequestDutyPtr     = Scan("E8 ?? ?? ?? ?? 33 C0 E9 ?? ?? ?? ?? FE C8");
        RequestRoulettePtr = Scan("48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 F9 48 8B DA");
        CancelPtr          = Scan("E8 ?? ?? ?? ?? C6 43 ?? ?? E8 ?? ?? ?? ?? 83 C0 ?? 89 43 ?? 48 83 C4 ?? 5B C3 C6 43");

        Service.Log.Info($"[Queue] native 排本: duty={RequestDutyPtr != 0} roulette={RequestRoulettePtr != 0} cancel={CancelPtr != 0}");
    }

    private static nint Scan(string signature)
    {
        try
        {
            // 与 DailyRoutines / OmenTools 一致：签名命中的就是函数入口，
            // 即使首字节是 E8（函数体第一条就是 call）也直接当函数指针用。
            return Service.Sig.ScanText(signature);
        }
        catch (Exception ex)
        {
            Service.Log.Warning($"[Queue] 签名扫描失败: {ex.Message}");
            return 0;
        }
    }

    // ---------- 查询 ----------

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

    // ---------- 排队 ----------

    /// <summary>排随机任务（导师随机 = 9）。</summary>
    internal static bool QueueRoulette(uint rouletteId)
    {
        if (RequestRoulettePtr != 0)
        {
            try
            {
                ContentsFinderOption option = ContentsFinderOption.Default;
                var fn = Marshal.GetDelegateForFunctionPointer<RequestRouletteDelegate>(RequestRoulettePtr);
                fn((ushort)rouletteId, ref option);
                Service.Log.Info($"[Queue] native RequestRoulette({rouletteId})");
                return true;
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Queue] native roulette 失败: {ex}");
            }
        }

        ContentsFinder* finder = ContentsFinder.Instance();

        if (finder == null)
            return false;

        Service.Log.Info($"[Queue] QueueInfo.QueueRoulette({rouletteId})");
        finder->QueueInfo.QueueRoulette((byte)rouletteId);
        return true;
    }

    /// <summary>排指定副本（ContentFinderCondition id）。</summary>
    internal static bool QueueDuty(uint cfcId)
    {
        if (cfcId == 0)
            return false;

        if (RequestDutyPtr != 0)
        {
            try
            {
                ContentsFinderOption option = ContentsFinderOption.Default;
                var fn = Marshal.GetDelegateForFunctionPointer<RequestDutyDelegate>(RequestDutyPtr);
                uint* ids = stackalloc uint[1];
                ids[0] = cfcId;

                bool ok = fn(ids, 1, 0, ref option);
                Service.Log.Info($"[Queue] native RequestDuty({cfcId}) -> {ok}");
                return ok;
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Queue] native duty 失败: {ex}");
            }
        }

        ContentsFinder* finder = ContentsFinder.Instance();

        if (finder == null)
            return false;

        uint* entries = stackalloc uint[1];
        entries[0] = cfcId;

        Service.Log.Info($"[Queue] QueueInfo.QueueDuties({cfcId})");
        finder->QueueInfo.QueueDuties(entries, 1);
        return true;
    }

    internal static void CancelQueue()
    {
        if (CancelPtr != 0)
        {
            try
            {
                var fn = Marshal.GetDelegateForFunctionPointer<CancelDelegate>(CancelPtr);
                fn(0);
                Service.Log.Info("[Queue] native Cancel");
                return;
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Queue] native cancel 失败: {ex}");
            }
        }

        ContentsFinder* finder = ContentsFinder.Instance();

        if (finder == null)
            return;

        Service.Log.Info("[Queue] QueueInfo.CancelQueue()");
        finder->QueueInfo.CancelQueue();
    }

    /// <summary>确认匹配弹出框（带节流与重试）。</summary>
    internal static bool AcceptPop() => DutyConfirm.TryAccept();

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
