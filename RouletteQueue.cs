namespace MentorRoulette;

using System;
using System.Collections.Generic;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using Lumina.Excel.Sheets;

/// <summary>
/// 排「随机任务」：用 AgentContentsFinder.OpenRouletteDuty 把随机加载进任务搜索器，再点加入。
/// </summary>
internal sealed unsafe class RouletteQueue
{
    private int      openAttempts;
    private bool     joinFired;
    private DateTime lastOpen = DateTime.MinValue;

    internal void Reset()
    {
        this.openAttempts = 0;
        this.joinFired    = false;
        this.lastOpen     = DateTime.MinValue;
    }

    internal static bool InQueue =>
        Conditions.Instance() != null && Conditions.Instance()->InDutyQueue;

    /// <summary>当前排的随机任务 id（不是随机任务时返回 0）。</summary>
    internal static byte QueuedRouletteId
    {
        get
        {
            FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder* finder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance();
            if (finder == null)
                return 0;

            var info = finder->GetQueueInfo();
            return info == null ? (byte)0 : info->QueuedContentRouletteId;
        }
    }

    internal void Tick(uint rouletteRowId)
    {
        if (this.AcceptPop())
            return;

        if (InQueue)
        {
            this.joinFired = true;
            return;
        }

        if (!Service.ClientState.IsLoggedIn || Service.Objects.LocalPlayer == null)
            return;

        if (!AddonHelper.TryGetReady("ContentsFinder", out AtkUnitBase* addon))
        {
            if ((DateTime.UtcNow - this.lastOpen).TotalSeconds < 3)
                return;

            this.lastOpen = DateTime.UtcNow;
            this.openAttempts++;

            if (this.openAttempts <= 4)
            {
                Service.Log.Debug($"[Queue] OpenRouletteDuty({rouletteRowId}) attempt {this.openAttempts}");
                AgentContentsFinder.Instance()->OpenRouletteDuty((byte)rouletteRowId);
            }
            else
            {
                Service.Log.Debug("[Queue] fallback to /dutyfinder");
                Service.Commands.ProcessCommand("/dutyfinder");
            }

            return;
        }

        if (this.joinFired)
            return;

        if (HasRouletteSelected())
        {
            Service.Log.Debug($"[Queue] roulette {rouletteRowId} selected, joining");
            this.joinFired = true;
            AddonHelper.Fire(addon, 12, 0);
            return;
        }

        this.SelectByName(addon, rouletteRowId);
    }

    private static bool HasRouletteSelected()
    {
        try
        {
            AgentContentsFinder* agent = AgentContentsFinder.Instance();
            return agent != null && agent->HasRouletteSelected;
        }
        catch (Exception ex)
        {
            Service.Log.Debug($"[Queue] HasRouletteSelected failed: {ex.Message}");
            return false;
        }
    }

    private bool AcceptPop()
    {
        if (!AddonHelper.TryGetReady("ContentsFinderConfirm", out AtkUnitBase* confirm))
            return false;

        Service.Log.Debug("[Queue] confirming duty pop");
        AddonHelper.Fire(confirm, 8);
        return true;
    }

    private void SelectByName(AtkUnitBase* addon, uint rouletteRowId)
    {
        string rouletteName = RouletteName(rouletteRowId);
        if (rouletteName.Length == 0)
            return;

        AddonContentsFinder* finder = (AddonContentsFinder*)addon;
        AtkComponentTreeList* dutyList = finder->DutyList;
        if (dutyList == null || dutyList->Items.LongCount == 0)
            return;

        List<AtkComponentTreeListItem> items = [];
        StdVector<Pointer<AtkComponentTreeListItem>> vector = dutyList->Items;
        vector.ForEach(p =>
                       {
                           if (p.Value != null)
                               items.Add(*p.Value);
                       });

        int index = -1;
        for (int i = 0; i < items.Count; i++)
        {
            string text = ItemText(items[i]);
            if (text.Length > 0 && text.Contains(rouletteName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        int headers = 0;
        for (int i = 0; i < index; i++)
            if (items[i].UIntValues[0] is 0 or 1)
                headers++;

        Service.Log.Debug($"[Queue] selecting '{rouletteName}' index {index} (arg {index + 1 - headers})");
        AddonHelper.Fire(addon, 3, index + 1 - headers);
    }

    private static string RouletteName(uint rouletteRowId)
    {
        try
        {
            Lumina.Excel.Sheets.ContentRoulette? row = Service.Data.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>().GetRowOrDefault(rouletteRowId);
            if (row.HasValue)
                return row.Value.Name.ToString().Trim();
        }
        catch (Exception ex)
        {
            Service.Log.Debug($"[Queue] ContentRoulette {rouletteRowId} name failed: {ex.Message}");
        }

        return string.Empty;
    }

    private static string ItemText(AtkComponentTreeListItem item)
    {
        try
        {
            AtkComponentListItemRenderer* renderer = item.Renderer;
            if (renderer == null)
                return string.Empty;

            AtkTextNode* node = renderer->GetTextNodeById(5);
            return node == null ? string.Empty : node->NodeText.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>调试：把当前搜索器列表打出来。</summary>
    internal static string DumpList()
    {
        if (!AddonHelper.TryGetReady("ContentsFinder", out AtkUnitBase* addon))
            return "任务搜索器没打开";

        AddonContentsFinder* finder = (AddonContentsFinder*)addon;
        AtkComponentTreeList* dutyList = finder->DutyList;
        if (dutyList == null)
            return "找不到 DutyList";

        List<AtkComponentTreeListItem> items = [];
        dutyList->Items.ForEach(p =>
                                {
                                    if (p.Value != null)
                                        items.Add(*p.Value);
                                });

        System.Text.StringBuilder sb = new();
        sb.AppendLine($"条目 {items.Count}，SelectedItemIndex={dutyList->SelectedItemIndex}，HasRouletteSelected={HasRouletteSelected()}");

        for (int i = 0; i < items.Count; i++)
            sb.AppendLine($"[{i}] u0={items[i].UIntValues[0]} '{ItemText(items[i])}'");

        return sb.ToString();
    }
}
