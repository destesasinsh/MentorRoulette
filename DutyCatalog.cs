namespace MentorRoulette;

using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Utility;
using Lumina.Excel;
using Lumina.Excel.Sheets;

internal sealed class DutyEntry
{
    internal uint   TerritoryId { get; init; }
    internal uint   CfcId       { get; init; }
    internal string Name        { get; init; } = string.Empty;
    internal byte   Level       { get; init; }
    internal bool   HasPath     { get; set; }
}

/// <summary>
/// 副本目录：从 ContentFinderCondition 取副本，向 AutoDuty 询问有没有路径。
/// </summary>
internal static class DutyCatalog
{
    private static readonly List<DutyEntry> entries = [];
    private static readonly Dictionary<uint, DutyEntry> byTerritory = [];
    private static DateTime lastRefresh = DateTime.MinValue;

    internal static IReadOnlyList<DutyEntry> Entries => entries;

    internal static bool IsKnownDuty(uint territoryId) => byTerritory.ContainsKey(territoryId);

    internal static DutyEntry? Get(uint territoryId) => byTerritory.GetValueOrDefault(territoryId);

    internal static void EnsureLoaded()
    {
        if (entries.Count > 0)
            return;

        ExcelSheet<ContentFinderCondition> sheet = Service.Data.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
            return;

        foreach (ContentFinderCondition cfc in sheet)
        {
            // 不按 ContentType 白名单裁剪：行会令、特殊副本等也要能选
            if (cfc.TerritoryType.ValueNullable is not { } territoryRow)
                continue;

            uint territory = territoryRow.RowId;

            if (territory == 0 || cfc.Name.ToString().IsNullOrEmpty() || byTerritory.ContainsKey(territory))
                continue;

            DutyEntry entry = new()
            {
                TerritoryId = territory,
                CfcId       = cfc.RowId,
                Name        = cfc.Name.ToDalamudString().TextValue.Trim(),
                Level       = cfc.ClassJobLevelRequired,
            };

            entries.Add(entry);
            byTerritory[territory] = entry;
        }

        entries.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : string.CompareOrdinal(a.Name, b.Name));
        Service.Log.Info($"DutyCatalog: {entries.Count} duties");
    }

    /// <summary>向 AutoDuty 刷新每个副本的“是否有路径”。</summary>
    internal static void RefreshSupport(AutoDutyIpc ipc)
    {
        EnsureLoaded();

        if (!ipc.Available)
            return;

        foreach (DutyEntry entry in entries)
            entry.HasPath = ipc.HasPath(entry.TerritoryId);

        lastRefresh = DateTime.UtcNow;
        Service.Log.Info($"DutyCatalog: refreshed, {entries.Count(e => e.HasPath)} supported");
    }

    internal static DateTime LastRefresh => lastRefresh;

    internal static int SupportedCount => entries.Count(e => e.HasPath);

    internal static IEnumerable<DutyEntry> SupportedEntries => entries.Where(e => e.HasPath);
}
