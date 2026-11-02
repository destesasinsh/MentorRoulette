namespace MentorRoulette.Sync;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

/// <summary>组队同步窗口（队长配置 + 成员只读）。</summary>
internal sealed class SyncWindow : Window
{
    private readonly Plugin plugin;
    private string joinCode  = string.Empty;
    private string dutySearch = string.Empty;
    private uint selectedDuty;
    private bool onlyAdSupported;

    internal SyncWindow(Plugin plugin) : base("组队同步###MentorRouletteSync")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(1100, 1000),
        };
        this.Size          = new Vector2(680, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Config Config => this.plugin.Config;
    private SyncCoordinator Sync => this.plugin.Sync;

    public override void Draw()
    {
        Config cfg = this.Config;

        if (DutyCatalog.Entries.Count == 0)
            DutyCatalog.EnsureLoaded();

        this.DrawConnection(cfg);
        ImGui.Separator();
        this.DrawAssignments(cfg);
        ImGui.Separator();
        this.DrawOptions(cfg);
        ImGui.Separator();
        this.DrawStatus();
    }

    private void DrawConnection(Config cfg)
    {
        string identity = this.Sync.Identity;
        if (identity.Length == 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "尚未登录角色");
        else
            ImGui.TextDisabled($"角色：{identity}");

        bool enabled = cfg.SyncEnabled;
        if (ImGui.Checkbox("启用组队同步", ref enabled))
        {
            cfg.SyncEnabled = enabled;
            cfg.Save();
        }

        if (!cfg.SyncEnabled)
            return;

        if (string.IsNullOrEmpty(cfg.SyncRoomCode))
        {
            ImGui.SetNextItemWidth(160);
            ImGui.InputText("组队码", ref this.joinCode, 16);

            ImGui.SameLine();
            using (ImRaii.Disabled(this.Sync.Identity.Length == 0))
            {
                if (ImGui.Button("加入房间"))
                {
                    string code = this.joinCode.ToUpperInvariant();
                    _ = Task.Run(async () =>
                                  {
                                      (bool ok, string msg) = await this.Sync.JoinRoomAsync(code);
                                      Service.Log.Info($"[Sync] join: {ok} {msg}");
                                  });
                }
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(this.Sync.Identity.Length == 0))
            {
                if (ImGui.Button("创建房间（我是队长）"))
                {
                    _ = Task.Run(async () =>
                                  {
                                      (bool ok, string msg) = await this.Sync.CreateRoomAsync();
                                      Service.Log.Info($"[Sync] create: {ok} {msg}");
                                  });
                }
            }
        }
        else
        {
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
        }

        if (this.Sync.LastError.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), this.Sync.LastError);
    }

    private void DrawAssignments(Config cfg)
    {
        if (!cfg.SyncIsHost)
        {
            ImGui.TextDisabled("成员端：等待队长下发配置。");
            return;
        }

        ImGui.Text("成员分配");

        ImGui.SameLine();
        if (ImGui.Button("快捷：讨伐彷徨死灵！"))
        {
            if (DutyCatalog.Get(190) != null)
            {
                this.AddDutyToSelection(cfg, 190);
                this.selectedDuty = 190;
                this.ApplySelectedDuty(cfg, 190);
                Service.Log.Info("[Sync] preset: 讨伐彷徨死灵！(190)");
            }
            else
            {
                Service.Log.Warning("[Sync] 没找到 190 号副本，先刷新副本列表");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("全部设为组队排本"))
        {
            foreach (ClientBox c in this.Sync.Room?.Clients.Values ?? Enumerable.Empty<ClientBox>())
            {
                if (!cfg.SyncAssignments.TryGetValue(c.Id, out Assignment? a))
                {
                    a = new Assignment();
                    cfg.SyncAssignments[c.Id] = a;
                }

                a.Role    = "party";
                a.PartyId = string.IsNullOrEmpty(a.PartyId) ? "A" : a.PartyId;
            }

            cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("单人导随设置…"))
            this.plugin.Window.IsOpen = true;

        List<ClientBox> clients = this.Sync.Room?.Clients.Values.OrderBy(c => c.Id).ToList() ?? [];
        if (clients.Count == 0)
        {
            ImGui.TextDisabled("还没有成员加入。");
            return;
        }

        string[] roles = ["不参与", "导随", "组队排本"];

        foreach (ClientBox c in clients)
        {
            if (!cfg.SyncAssignments.TryGetValue(c.Id, out Assignment? a))
            {
                a = new Assignment { Role = "none" };
                cfg.SyncAssignments[c.Id] = a;
            }

            ImGui.TextColored(c.Online ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f), "●");

            ImGui.SameLine();
            ImGui.TextUnformatted($"{c.Name} ({c.Id})");

            ImGui.SameLine();
            int roleIndex = a.Role switch { "roulette" => 1, "party" => 2, _ => 0 };
            ImGui.SetNextItemWidth(120);
            if (ImGui.Combo($"##role{c.Id}", ref roleIndex, roles, roles.Length))
            {
                a.Role = roleIndex switch { 1 => "roulette", 2 => "party", _ => "none" };
                cfg.Save();
            }

            if (a.Role == "party")
            {
                ImGui.SameLine();

                string dutyName = a.Territory == 0
                                      ? "未选择"
                                      : DutyCatalog.Get(a.Territory)?.Name ?? $"#{a.Territory}";

                ImGui.TextColored(a.Territory == 0 ? new Vector4(1f, 0.7f, 0.3f, 1f) : new Vector4(0.5f, 0.9f, 0.5f, 1f),
                                  $"副本：{dutyName} ({a.Territory})");

                ImGui.SameLine();
                string partyId = a.PartyId;
                ImGui.SetNextItemWidth(60);
                if (ImGui.InputText($"##party{c.Id}", ref partyId, 8))
                {
                    a.PartyId = partyId;
                    cfg.Save();
                }

            }

            string state = c.Report == null ? "—" : $"{c.Report.State} {(c.Report.Territory != 0 ? $"·{c.Report.Territory}" : string.Empty)}";
            ImGui.SameLine();
            ImGui.TextDisabled(state);
        }

        ImGui.Separator();
        this.DrawDutySelector(cfg);
        ImGui.Separator();

        if (ImGui.Button("下发配置"))
            this.Sync.PushConfig();

        ImGui.SameLine();
        if (ImGui.Button("开始本轮"))
            this.Sync.StartRound();

        ImGui.SameLine();
        if (ImGui.Button("中止"))
            this.Sync.Abort();

        ImGui.SameLine();
        if (ImGui.Button("下一轮"))
            this.Sync.NextRound();
    }

    private void DrawDutySelector(Config cfg)
    {
        if (!ImGui.CollapsingHeader($"副本选择（已选 {cfg.SyncSelectedDuties.Count}）###dutySelector"))
            return;

        float height = 220f;
        float width  = MathF.Max(160f, (ImGui.GetContentRegionAvail().X - 12f) * 0.5f);

        using (ImRaii.Child("##dutyLeft", new Vector2(width, height), true))
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##dutySearch", "搜索待选副本…", ref this.dutySearch, 64);
            ImGui.Checkbox("只看 AutoDuty 支持", ref this.onlyAdSupported);

            foreach (DutyEntry duty in this.onlyAdSupported ? DutyCatalog.SupportedEntries : DutyCatalog.Entries)
            {
                if (this.dutySearch.Length > 0 && !duty.Name.Contains(this.dutySearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool already = cfg.SyncSelectedDuties.Contains(duty.TerritoryId);

                if (ImGui.Selectable($"{duty.Name} ({duty.TerritoryId})##left{duty.TerritoryId}", false))
                {
                    this.AddDutyToSelection(cfg, duty.TerritoryId);
                    this.selectedDuty = duty.TerritoryId;
                }

                if (already)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("· 已选");
                }
            }
        }

        ImGui.SameLine();

        using (ImRaii.Child("##dutyRight", new Vector2(0f, height), true))
        {
            if (cfg.SyncSelectedDuties.Count == 0)
                ImGui.TextDisabled("点左边的副本加到这里");

            foreach (uint territory in cfg.SyncSelectedDuties.ToList())
            {
                string name = DutyCatalog.Get(territory)?.Name ?? $"#{territory}";

                ImGui.PushID((int)territory);

                bool inUse = cfg.SyncAssignments.Values.Any(x => x.Role == "party" && x.Territory == territory);

                if (ImGui.Selectable($"{name} ({territory})", this.selectedDuty == territory))
                    this.selectedDuty = territory;

                if (inUse)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "· 使用中");
                }

                ImGui.SameLine();
                if (ImGui.Button("移除"))
                {
                    cfg.SyncSelectedDuties.Remove(territory);

                    if (this.selectedDuty == territory)
                        this.selectedDuty = 0;

                    cfg.Save();
                }

                ImGui.PopID();
            }
        }

        if (ImGui.Button("把选中的副本设为组队端副本"))
        {
            uint target = this.selectedDuty != 0 ? this.selectedDuty : cfg.SyncSelectedDuties.FirstOrDefault();

            if (target != 0)
                this.ApplySelectedDuty(cfg, target);
        }

        ImGui.SameLine();
        if (ImGui.Button("清空已选"))
        {
            cfg.SyncSelectedDuties.Clear();
            this.selectedDuty = 0;
            cfg.Save();
        }
    }

    private void AddDutyToSelection(Config cfg, uint territory)
    {
        if (!cfg.SyncSelectedDuties.Contains(territory))
            cfg.SyncSelectedDuties.Add(territory);

        cfg.Save();
    }

    private void ApplySelectedDuty(Config cfg, uint territory)
    {
        DutyEntry? duty = DutyCatalog.Get(territory);
        if (duty == null)
        {
            Service.Log.Warning($"[Sync] 副本 {territory} 不在列表里");
            return;
        }

        foreach (Assignment a in cfg.SyncAssignments.Values.Where(x => x.Role == "party"))
        {
            a.Territory = duty.TerritoryId;
            a.Cfc       = duty.CfcId;
        }

        cfg.Save();
        Service.Log.Info($"[Sync] 组队端副本 -> {duty.Name} ({duty.TerritoryId})");
    }

    private void DrawOptions(Config cfg)
    {
        if (!cfg.SyncIsHost)
            return;

        RoomOptions opt = cfg.SyncOptions;

        string[] penaltyModes = ["全员等待惩罚", "仅退本端等待"];
        int penaltyIndex = opt.PenaltyMode == "leaver" ? 1 : 0;
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("惩罚处理", ref penaltyIndex, penaltyModes, penaltyModes.Length))
        {
            opt.PenaltyMode = penaltyIndex == 1 ? "leaver" : "all";
            cfg.Save();
        }

        int successMin = opt.SuccessMin;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("成功所需导随端数", ref successMin))
        {
            opt.SuccessMin = Math.Clamp(successMin, 1, 4);
            cfg.Save();
        }

        int timeout = opt.QueueTimeoutSec;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("排队超时 (秒)", ref timeout))
        {
            opt.QueueTimeoutSec = Math.Clamp(timeout, 30, 1800);
            cfg.Save();
        }

        int staggerParty = cfg.SyncStagger.Party;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("组队端错开 (ms)", ref staggerParty))
        {
            cfg.SyncStagger.Party = Math.Clamp(staggerParty, 0, 10000);
            cfg.Save();
        }

        int staggerRoulette = cfg.SyncStagger.Roulette;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("导随端错开 (ms)", ref staggerRoulette))
        {
            cfg.SyncStagger.Roulette = Math.Clamp(staggerRoulette, 0, 10000);
            cfg.Save();
        }

        bool autoLoop = opt.AutoLoop;
        if (ImGui.Checkbox("自动下一轮", ref autoLoop))
        {
            opt.AutoLoop = autoLoop;
            cfg.Save();
        }

        ImGui.SameLine();
        int maxRounds = opt.MaxRounds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("轮次上限", ref maxRounds))
        {
            opt.MaxRounds = Math.Clamp(maxRounds, 1, 999);
            cfg.Save();
        }

        ImGui.SameLine();
        int interval = opt.RoundIntervalSec;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("每轮间隔 (秒)", ref interval))
        {
            opt.RoundIntervalSec = Math.Clamp(interval, 0, 600);
            cfg.Save();
        }

        string[] postModes = ["进本后什么都不做", "执行命令/宏", "启动 AutoDuty", "两者都做"];
        int postIndex = opt.PostEntry.Mode switch { "command" => 1, "autoduty" => 2, "both" => 3, _ => 0 };
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("进本后动作", ref postIndex, postModes, postModes.Length))
        {
            opt.PostEntry.Mode = postIndex switch { 1 => "command", 2 => "autoduty", 3 => "both", _ => "nothing" };
            cfg.Save();
        }

        if (opt.PostEntry.Mode is "command" or "both" || opt.PostEntry.Commands.Length > 0)
        {
            string commands = opt.PostEntry.Commands;
            if (ImGui.InputTextMultiline("进本后执行的命令（一行一条，按顺序发）", ref commands, 4096, new Vector2(420, 90)))
            {
                opt.PostEntry.Commands = commands;
                cfg.Save();
            }

            ImGui.TextDisabled("只在「进对了本」时执行；# 开头是注释；不带 / 会自动补上");
        }
    }

    private void DrawStatus()
    {
        RoomState? room = this.Sync.Room;

        ImGui.Text($"我的角色：{this.Sync.MyRole}");

        if (room != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"第 {room.Round} / {room.Options.MaxRounds} 轮");

            ImGui.SameLine();
            ImGui.TextDisabled(room.Phase switch
            {
                "queueing" => "排队中",
                "armed"    => "已下发配置",
                "idle"     => "待机",
                _          => room.Phase,
            });
        }

        ImGui.TextDisabled(this.Sync.Status);

        DateTime penalty = this.Sync.PenaltyUntil;
        if (penalty > DateTime.UtcNow)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), $"本地惩罚剩余 {(penalty - DateTime.UtcNow):mm\\:ss}");
    }
}
