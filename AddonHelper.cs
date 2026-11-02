namespace MentorRoulette;

using FFXIVClientStructs.FFXIV.Component.GUI;

internal static unsafe class AddonHelper
{
    internal static bool TryGetReady(string name, out AtkUnitBase* addon)
    {
        addon = null;

        nint ptr = Service.GameGui.GetAddonByName(name, 1);
        if (ptr == nint.Zero)
            return false;

        addon = (AtkUnitBase*)ptr;

        return addon != null &&
               addon->IsVisible &&
               addon->UldManager.LoadedState == AtkLoadState.Loaded;
    }

    /// <summary>给 addon 发回调，参数统一按 int 处理（足够覆盖任务搜索器/确认框）。</summary>
    internal static void Fire(AtkUnitBase* addon, params int[] values) =>
        Fire(addon, true, values);

    internal static void Fire(AtkUnitBase* addon, bool updateState, params int[] values)
    {
        if (addon == null)
            return;

        AtkValue* atkValues = stackalloc AtkValue[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            atkValues[i]      = default;
            atkValues[i].Type = AtkValueType.Int;
            atkValues[i].Int  = values[i];
        }

        addon->FireCallback((uint)values.Length, atkValues, updateState);
    }
}
