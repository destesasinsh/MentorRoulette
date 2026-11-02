namespace MentorRoulette;

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

/// <summary>
/// 游戏原生 ExecuteCommand —— DailyRoutines / OmenTools 的退本、确认进本都走它。
/// CS 里有 GameMain+MemberFunctionPointers.ExecuteCommand（函数指针属性），
/// 这里用反射取地址再转成委托调用；拿不到就返回 false，调用方退回界面路径。
/// </summary>
internal static class NativeCommand
{
    internal const uint NotifyTerritoryReady = 816;
    internal const uint LeaveDuty            = 819;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool ExecuteCommandDelegate(uint flag, uint p1, uint p2, uint p3, uint p4);

    private static readonly ExecuteCommandDelegate? Fn;

    internal static bool Available => Fn != null;

    static NativeCommand()
    {
        try
        {
            Type? type = typeof(AgentModule).Assembly.GetType("FFXIVClientStructs.FFXIV.Client.Game.GameMain+MemberFunctionPointers");

            if (type == null)
            {
                Service.Log.Info("[Native] 没有 GameMain+MemberFunctionPointers");
                return;
            }

            PropertyInfo? prop = type.GetProperty("ExecuteCommand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (prop == null)
            {
                Service.Log.Info("[Native] 没有 ExecuteCommand 成员指针");
                return;
            }

            object? value = prop.GetValue(null);

            if (value is not IntPtr address || address == IntPtr.Zero)
            {
                Service.Log.Warning($"[Native] ExecuteCommand 指针无效: {value}");
                return;
            }

            Fn = Marshal.GetDelegateForFunctionPointer<ExecuteCommandDelegate>(address);
            Service.Log.Info($"[Native] ExecuteCommand @ 0x{address.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Service.Log.Warning($"[Native] 初始化失败: {ex.Message}");
        }
    }

    internal static bool Execute(uint flag, uint p1 = 0, uint p2 = 0, uint p3 = 0, uint p4 = 0)
    {
        if (Fn == null)
            return false;

        try
        {
            bool ok = Fn(flag, p1, p2, p3, p4);
            Service.Log.Info($"[Native] ExecuteCommand({flag}) -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[Native] ExecuteCommand({flag}) 异常: {ex.Message}");
            return false;
        }
    }
}
