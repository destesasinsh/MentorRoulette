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
    private string? dutyPickerFor;
    private bool    onlyAdSupported;

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
        ImGui.SetNextItemWidth(260);
        string url = cfg.SyncServerUrl;
        if (ImGui.InputText("服务器地址", ref url, 256))
        {
            cfg.SyncServerUrl = url;
            cfg.Save();
        }

        ImGui.SetNextItemWidth(160);
        string name = cfg.SyncClientName;
        if (ImGui.InputText("我的名字", ref name, 64))
        {
            cfg.SyncClientName = name;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"ID: {cfg.EnsureClientId()}");

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
            if (ImGui.Button("加入房间"))
            {
                string code = this.joinCode.ToUpperInvariant();
                _ = Task.Run(async () =>
                              {
                                  (bool ok, string msg) = await this.Sync.JoinRoomAsync(code);
                                  Service.Log.Info($"[Sync] join: {ok} {msg}");
                              });
            }

            ImGui.SameLine();
            if (ImGui.Button("创建房间（我是队长）"))
            {
                string hostName = cfg.SyncClientName;
                _ = Task.Run(async () =>
                              {
                                  (bool ok, string msg) = await this.Sync.CreateRoomAsync(hostName);
                                  Service.Log.Info($"[Sync] create: {ok} {msg}");
                              });
            }
        }
        else
        {
            ImGui.TextColored(this.Sync.Connected ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.5f, 0.5f, 1f),
                              $"房间 {cfg.SyncRoomCode} · {(cfg.SyncIsHost ? "队长" : "成员")} · {(this.Sync.Connected ? "已连接" : "未连接")}");

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
                string dutyName = a.Territory == 0 ? "选择副本…" : DutyCatalog.Get(a.Territory)?.Name ?? a.Territory.ToString();
                if (ImGui.Button($"{dutyName}##pick{c.Id}"))
                {
                    this.dutyPickerFor  = c.Id;
                    this.dutySearch     = string.Empty;
                    ImGui.OpenPopup("dutyPicker");
                }

                ImGui.SameLine();
                string partyId = a.PartyId;
                ImGui.SetNextItemWidth(60);
                if (ImGui.InputText($"##party{c.Id}", ref partyId, 8))
                {
                    a.PartyId = partyId;
                    cfg.Save();
                }

                if (this.dutyPickerFor == c.Id)
                    this.DrawDutyPicker(cfg, a);
            }

            string state = c.Report == null ? "—" : $"{c.Report.State} {(c.Report.Territory != 0 ? $"·{c.Report.Territory}" : string.Empty)}";
            ImGui.SameLine();
            ImGui.TextDisabled(state);
        }

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

    private void DrawDutyPicker(Config cfg, Assignment a)
    {
        if (!ImGui.BeginPopup("dutyPicker"))
            return;

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##dutySearch", "搜索…", ref this.dutySearch, 64);

        ImGui.SameLine();
        ImGui.Checkbox("只看 AutoDuty 支持", ref this.onlyAdSupported);

        using (ImRaii.Child("##dutyPickerList", new Vector2(340, 280), true))
        {
            foreach (DutyEntry duty in this.onlyAdSupported ? DutyCatalog.SupportedEntries : DutyCatalog.Entries)
            {
                if (this.dutySearch.Length > 0 && !duty.Name.Contains(this.dutySearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                string label = duty.HasPath ? $"{duty.Name}##{duty.TerritoryId}" : $"{duty.Name}（无 AD 路径）##{duty.TerritoryId}";

                if (!ImGui.Selectable(label))
                    continue;

                a.Territory = duty.TerritoryId;
                a.Cfc       = duty.CfcId;
                cfg.Save();
                this.dutyPickerFor = null;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndPopup();
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

        if (opt.PostEntry.Mode is "command" or "both")
        {
            ImGui.SetNextItemWidth(260);
            string command = opt.PostEntry.Command;
            if (ImGui.InputText("命令（可含 /）", ref command, 256))
            {
                opt.PostEntry.Command = command;
                cfg.Save();
            }
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
