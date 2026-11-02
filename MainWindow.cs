namespace MentorRoulette;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using MentorRoulette.Sync;

/// <summary>唯一的设置窗口：组队同步 / 单人导随共用一套规则。</summary>
internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;

    private string joinCode      = string.Empty;
    private string dutySearch    = string.Empty;
    private bool   onlyAdSupported;
    private uint   highlightedDuty;

    private string[] rouletteLabels = [];
    private uint[]   rouletteIds    = [];

    internal MainWindow(Plugin plugin) : base("导随助手###MentorRouletteMain")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 480),
            MaximumSize = new Vector2(1200, 1400),
        };

        this.Size          = new Vector2(780, 840);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Config Config => this.plugin.Config;
    private SyncCoordinator Sync => this.plugin.Sync;
    private Manager Solo => this.plugin.Manager;

    public override void Draw()
    {
        if (DutyCatalog.Entries.Count == 0)
            DutyCatalog.EnsureLoaded();

        if (!ImGui.BeginTabBar("##mainTabs", ImGuiTabBarFlags.None))
            return;

        if (ImGui.BeginTabItem("设置"))
        {
            this.DrawSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("日志"))
        {
            this.DrawLogTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawSettings()
    {
        Config cfg = this.Config;
        bool group = cfg.LaunchMode == LaunchMode.Group;

        this.DrawMode(cfg, group);
        ImGui.Separator();

        if (group)
        {
            this.DrawRoom(cfg);
            ImGui.Separator();
            this.DrawMembers(cfg);
            ImGui.Separator();
        }

        this.DrawTargets(cfg);
        ImGui.Separator();
        this.DrawPostEntry(cfg);
        ImGui.Separator();
        this.DrawWrongDuty(cfg, group);
        ImGui.Separator();
        this.DrawLoop(cfg);
        ImGui.Separator();
        this.DrawMisc(cfg);
        ImGui.Separator();
        this.DrawButtons(cfg, group);
    }

    // ---------- 运行方式 ----------

    private void DrawMode(Config cfg, bool group)
    {
        if (ImGui.RadioButton("组队同步", group))
        {
            cfg.LaunchMode = LaunchMode.Group;
            cfg.Save();
        }

        ImGui.SameLine();

        if (ImGui.RadioButton("单人导随", !group))
        {
            cfg.LaunchMode = LaunchMode.Solo;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(group ? this.Sync.Status : this.Solo.Status);
    }

    // ---------- 房间 ----------

    private void DrawRoom(Config cfg)
    {
        if (!ImGui.CollapsingHeader("房间###room"))
            return;

        string identity = this.Sync.Identity;

        if (string.IsNullOrEmpty(cfg.SyncRoomCode))
        {
            if (identity.Length == 0)
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "尚未登录角色");

            using (ImRaii.Disabled(identity.Length == 0))
            {
                if (ImGui.Button("创建房间（我是队长）"))
                    _ = Task.Run(async () => DiagLog.Add((await this.Sync.CreateRoomAsync()).msg));

                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.InputText("##joinCode", ref this.joinCode, 16);
                ImGui.SameLine();

                if (ImGui.Button("加入房间"))
                {
                    string code = this.joinCode.ToUpperInvariant();
                    _ = Task.Run(async () => DiagLog.Add((await this.Sync.JoinRoomAsync(code)).msg));
                }
            }

            return;
        }

        ImGui.TextColored(this.Sync.Connected ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.5f, 0.5f, 1f),
                          $"房间 {cfg.SyncRoomCode} · {(cfg.SyncIsHost ? "队长" : "成员")} · {(this.Sync.Connected ? "已连接" : "未连接")}");

        ImGui.SameLine();
        if (ImGui.Button("复制房间号"))
            ImGui.SetClipboardText(cfg.SyncRoomCode);

        ImGui.SameLine();
        if (ImGui.Button("断开"))
        {
            this.Sync.Disconnect();
            cfg.SyncRoomCode = string.Empty;
            cfg.SyncIsHost   = false;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"我是 {identity}");
    }

    // ---------- 成员与分工 ----------

    private void DrawMembers(Config cfg)
    {
        if (!ImGui.CollapsingHeader("成员与分工###members"))
            return;

        string[] roles = ["不参与", "导随", "组队排本"];

        if (!cfg.SyncIsHost)
        {
            ImGui.TextDisabled("成员端：等待队长下发分工。");
            return;
        }

        List<ClientBox> clients = this.Sync.Room?.Clients.Values.OrderBy(c => c.Id).ToList() ?? [];

        if (clients.Count == 0)
        {
            ImGui.TextDisabled("还没有成员加入。");
            return;
        }

        foreach (ClientBox c in clients)
        {
            if (!cfg.SyncAssignments.TryGetValue(c.Id, out Assignment? a))
            {
                a = new Assignment { Role = "none" };
                cfg.SyncAssignments[c.Id] = a;
            }

            ImGui.TextColored(c.Online ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f), "●");
            ImGui.SameLine();
            ImGui.TextUnformatted(c.Id);

            ImGui.SameLine();
            int roleIndex = a.Role switch { "roulette" => 1, "party" => 2, _ => 0 };
            ImGui.SetNextItemWidth(110);

            if (ImGui.Combo($"##role{c.Id}", ref roleIndex, roles, roles.Length))
            {
                a.Role = roleIndex switch { 1 => "roulette", 2 => "party", _ => "none" };
                cfg.Save();
            }

            if (a.Role == "party")
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(50);
                string partyId = a.PartyId;
                if (ImGui.InputText($"##party{c.Id}", ref partyId, 8))
                {
                    a.PartyId = partyId;
                    cfg.Save();
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled(c.Report == null
                                   ? "—"
                                   : $"{c.Report.State}{(c.Report.Territory != 0 ? $" ·{c.Report.Territory}" : string.Empty)}");
        }

        if (ImGui.Button("全部设为组队排本"))
        {
            foreach (ClientBox c in clients)
            {
                cfg.SyncAssignments[c.Id] = cfg.SyncAssignments.TryGetValue(c.Id, out Assignment? a)
                                                ? a
                                                : new Assignment();

                cfg.SyncAssignments[c.Id].Role    = "party";
                cfg.SyncAssignments[c.Id].PartyId = string.IsNullOrEmpty(cfg.SyncAssignments[c.Id].PartyId)
                                                        ? "A"
                                                        : cfg.SyncAssignments[c.Id].PartyId;
            }

            cfg.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("全部设为导随"))
        {
            foreach (ClientBox c in clients)
            {
                if (!cfg.SyncAssignments.TryGetValue(c.Id, out Assignment? a))
                {
                    a = new Assignment();
                    cfg.SyncAssignments[c.Id] = a;
                }

                a.Role = "roulette";
            }

            cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("下发分工"))
            this.Sync.PushConfig();
    }

    // ---------- 排队目标 ----------

    private void DrawTargets(Config cfg)
    {
        if (!ImGui.CollapsingHeader("排队目标###targets"))
            return;

        this.EnsureRouletteLabels();

        int rouletteIndex = Math.Max(0, Array.IndexOf(this.rouletteIds, cfg.RouletteId));

        ImGui.SetNextItemWidth(240);

        if (this.rouletteLabels.Length > 0 &&
            ImGui.Combo("随机任务", ref rouletteIndex, this.rouletteLabels, this.rouletteLabels.Length))
        {
            cfg.RouletteId = this.rouletteIds[Math.Clamp(rouletteIndex, 0, this.rouletteIds.Length - 1)];
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("副本清单（空 = 放行全部 AutoDuty 支持的本）");

        float height = 200f;
        float width  = MathF.Max(180f, (ImGui.GetContentRegionAvail().X - 12f) * 0.5f);

        using (ImRaii.Child("##poolLeft", new Vector2(width, height), true))
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##dutySearch", "搜索待选副本…", ref this.dutySearch, 64);
            ImGui.Checkbox("只看 AutoDuty 支持", ref this.onlyAdSupported);

            IEnumerable<DutyEntry> source = this.onlyAdSupported ? DutyCatalog.SupportedEntries : DutyCatalog.Entries;

            foreach (DutyEntry duty in source)
            {
                if (this.dutySearch.Length > 0 && !duty.Name.Contains(this.dutySearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool already = cfg.DutyPool.Contains(duty.TerritoryId);
                string label = $"{duty.Name} ({duty.TerritoryId})##left{duty.TerritoryId}";

                if (ImGui.Selectable(label, this.highlightedDuty == duty.TerritoryId))
                {
                    if (!already)
                        cfg.DutyPool.Add(duty.TerritoryId);

                    this.highlightedDuty = duty.TerritoryId;
                    cfg.Save();
                }

                if (already)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("· 已选");
                }
            }
        }

        ImGui.SameLine();

        using (ImRaii.Child("##poolRight", new Vector2(0f, height), true))
        {
            if (cfg.DutyPool.Count == 0)
                ImGui.TextDisabled("点左边的副本加进来");

            foreach (uint territory in cfg.DutyPool.ToList())
            {
                string name = DutyCatalog.Get(territory)?.Name ?? $"#{territory}";
                bool inUse  = cfg.PartyDutyTerritory == territory;

                ImGui.PushID((int)territory);

                if (ImGui.Selectable($"{name} ({territory})", this.highlightedDuty == territory))
                    this.highlightedDuty = territory;

                if (inUse)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "· 组队端使用中");
                }

                ImGui.SameLine();

                if (ImGui.Button("移除"))
                {
                    cfg.DutyPool.Remove(territory);

                    if (cfg.PartyDutyTerritory == territory)
                        cfg.PartyDutyTerritory = 0;

                    cfg.Save();
                }

                ImGui.PopID();
            }
        }

        if (ImGui.Button("把选中的副本设为组队端副本"))
        {
            uint target = this.highlightedDuty != 0 ? this.highlightedDuty : cfg.DutyPool.FirstOrDefault();

            if (target != 0)
            {
                cfg.PartyDutyTerritory = target;
                cfg.Save();
                DiagLog.Add($"组队端副本 -> {DutyCatalog.Get(target)?.Name ?? target.ToString()} ({target})");
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("清空清单"))
        {
            cfg.DutyPool.Clear();
            cfg.PartyDutyTerritory = 0;
            cfg.Save();
        }
    }

    private void EnsureRouletteLabels()
    {
        if (this.rouletteLabels.Length == DutyCatalog.Roulettes.Count && DutyCatalog.Roulettes.Count > 0)
            return;

        this.rouletteLabels = DutyCatalog.Roulettes.Select(r => $"{r.Name} ({r.Id})").ToArray();
        this.rouletteIds    = DutyCatalog.Roulettes.Select(r => r.Id).ToArray();
    }

    // ---------- 进本后 ----------

    private void DrawPostEntry(Config cfg)
    {
        if (!ImGui.CollapsingHeader("进本后###postEntry"))
            return;

        PostEntry post = cfg.PostEntry;

        string[] modes = ["什么都不做", "执行宏", "启动 AutoDuty", "两者都做"];
        int index = post.Mode switch { "command" => 1, "autoduty" => 2, "both" => 3, _ => 0 };

        for (int i = 0; i < modes.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine();

            if (!ImGui.RadioButton(modes[i], index == i))
                continue;

            post.Mode = i switch { 1 => "command", 2 => "autoduty", 3 => "both", _ => "nothing" };
            cfg.Save();
        }

        if (post.Mode is "command" or "both")
        {
            string commands = post.Commands;

            if (ImGui.InputTextMultiline("##commands", ref commands, 4096, new Vector2(560, 80)))
            {
                post.Commands = commands;
                cfg.Save();
            }

            ImGui.TextDisabled("一行一条，按顺序发（600ms 间隔）；# 开头是注释；不带 / 会自动补上");
        }
    }

    // ---------- 进错 / 不支持 ----------

    private void DrawWrongDuty(Config cfg, bool group)
    {
        if (!ImGui.CollapsingHeader("进错 / 不支持时###wrongDuty"))
            return;

        bool leave = cfg.Unsupported == UnsupportedAction.LeaveDuty;

        if (ImGui.RadioButton("退本（吃 30 分钟惩罚）", leave))
        {
            cfg.Unsupported = UnsupportedAction.LeaveDuty;
            cfg.Save();
        }

        ImGui.SameLine();

        if (ImGui.RadioButton("原地挂机", !leave))
        {
            cfg.Unsupported = UnsupportedAction.KeepDuty;
            cfg.Save();
        }

        bool force = cfg.ForceLeaveDuty;
        if (ImGui.Checkbox("立即退本（不等脱战）", ref force))
        {
            cfg.ForceLeaveDuty = force;
            cfg.Save();
        }

        using (ImRaii.Disabled(!group))
        {
            bool all = cfg.PenaltyMode != "leaver";

            if (ImGui.RadioButton("全员一起等", all))
            {
                cfg.PenaltyMode = "all";
                cfg.Save();
            }

            ImGui.SameLine();

            if (ImGui.RadioButton("只有退本的那端等", !all))
            {
                cfg.PenaltyMode = "leaver";
                cfg.Save();
            }
        }
    }

    // ---------- 循环 ----------

    private void DrawLoop(Config cfg)
    {
        if (!ImGui.CollapsingHeader("循环###loop"))
            return;

        bool auto = cfg.AutoLoop;
        if (ImGui.Checkbox("自动下一轮", ref auto))
        {
            cfg.AutoLoop = auto;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int interval = cfg.RoundIntervalSec;
        if (ImGui.InputInt("每轮间隔 (秒)", ref interval))
        {
            cfg.RoundIntervalSec = Math.Clamp(interval, 0, 600);
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int maxRounds = cfg.MaxRounds;
        if (ImGui.InputInt("轮次上限", ref maxRounds))
        {
            cfg.MaxRounds = Math.Clamp(maxRounds, 1, 999);
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int successMin = cfg.SuccessMin;
        if (ImGui.InputInt("成功所需导随端", ref successMin))
        {
            cfg.SuccessMin = Math.Clamp(successMin, 1, 4);
            cfg.Save();
        }
    }

    // ---------- 其它 ----------

    private void DrawMisc(Config cfg)
    {
        if (!ImGui.CollapsingHeader("其它###misc"))
            return;

        bool notify = cfg.Notify;
        if (ImGui.Checkbox("游戏内提示", ref notify))
        {
            cfg.Notify = notify;
            cfg.Save();
        }

        ImGui.TextDisabled($"已排 {cfg.QueuedCount} / 进本 {cfg.JoinedCount} / 完成 {cfg.CompletedCount} / 退出 {cfg.LeftCount}");

        RoomState? room = this.Sync.Room;
        if (room != null)
            ImGui.TextDisabled($"第 {room.Round} / {room.Options.MaxRounds} 轮 · {room.Phase}");
    }

    // ---------- 按钮 ----------

    private void DrawButtons(Config cfg, bool group)
    {
        if (group)
        {
            using (ImRaii.Disabled(!cfg.SyncIsHost || this.Sync.Room == null))
            {
                if (ImGui.Button("开始本轮"))
                    this.Sync.StartRound();

                ImGui.SameLine();

                if (ImGui.Button("中止"))
                    this.Sync.Abort();

                ImGui.SameLine();

                if (ImGui.Button("下一轮"))
                    this.Sync.NextRound();
            }

            return;
        }

        bool enabled = cfg.Enabled;

        if (ImGui.Checkbox("启用", ref enabled))
        {
            if (enabled)
                this.Solo.Start();
            else
                this.Solo.Stop("界面关闭");

            cfg.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("开始"))
            this.Solo.Start();

        ImGui.SameLine();

        if (ImGui.Button("停止"))
            this.Solo.Stop("界面停止");
    }

    // ---------- 日志 ----------

    private void DrawLogTab()
    {
        if (ImGui.Button("清空"))
            DiagLog.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled($"{DiagLog.Count} 条");

        ImGui.SameLine();
        if (ImGui.Button("复制全部"))
            ImGui.SetClipboardText(string.Join(Environment.NewLine, DiagLog.Snapshot()));

        ImGui.Separator();

        List<string> lines = DiagLog.Snapshot();

        using (ImRaii.Child("##logBody", new Vector2(0, 0), true))
        {
            foreach (string line in lines)
                ImGui.TextUnformatted(line);

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 24f)
                ImGui.SetScrollHereY(1f);
        }
    }
}
