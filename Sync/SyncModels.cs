namespace MentorRoulette.Sync;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class RoomState
{
    [JsonPropertyName("code")]       public string Code { get; set; } = string.Empty;
    [JsonPropertyName("rev")]        public long Rev { get; set; }
    [JsonPropertyName("phase")]      public string Phase { get; set; } = "idle";
    [JsonPropertyName("round")]      public int Round { get; set; }
    [JsonPropertyName("startAt")]    public long StartAt { get; set; }
    [JsonPropertyName("stagger")]    public Stagger Stagger { get; set; } = new();
    [JsonPropertyName("assignments")] public Dictionary<string, Assignment> Assignments { get; set; } = [];
    [JsonPropertyName("options")]    public RoomOptions Options { get; set; } = new();
    [JsonPropertyName("clients")]    public Dictionary<string, ClientBox> Clients { get; set; } = [];
    [JsonPropertyName("commands")]   public List<Command> Commands { get; set; } = [];
}

public sealed class Stagger
{
    [JsonPropertyName("party")]    public int Party { get; set; }
    [JsonPropertyName("roulette")] public int Roulette { get; set; }
}

public sealed class Assignment
{
    [JsonPropertyName("role")]        public string Role { get; set; } = "none"; // roulette | party | none
    [JsonPropertyName("rouletteId")]  public uint RouletteId { get; set; } = 9;
    [JsonPropertyName("partyId")]     public string PartyId { get; set; } = string.Empty;
    [JsonPropertyName("territory")]   public uint Territory { get; set; }
    [JsonPropertyName("cfc")]         public uint Cfc { get; set; }
}

public sealed class RoomOptions
{
    [JsonPropertyName("penaltyMode")]     public string PenaltyMode { get; set; } = "all"; // all | leaver
    [JsonPropertyName("successMin")]      public int SuccessMin { get; set; } = 1;
    [JsonPropertyName("queueTimeoutSec")] public int QueueTimeoutSec { get; set; } = 300;
    [JsonPropertyName("autoLoop")]        public bool AutoLoop { get; set; }
    [JsonPropertyName("maxRounds")]       public int MaxRounds { get; set; } = 20;
    [JsonPropertyName("roundIntervalSec")] public int RoundIntervalSec { get; set; } = 10;
    [JsonPropertyName("postEntry")]       public PostEntry PostEntry { get; set; } = new();
}

public sealed class PostEntry
{
    [JsonPropertyName("mode")]      public string Mode { get; set; } = "nothing"; // nothing | command | autoduty | both
    [JsonPropertyName("command")]   public string Command { get; set; } = string.Empty;
    /// <summary>进本后要执行的命令，一行一条，按顺序发。</summary>
    [JsonPropertyName("commands")]  public string Commands { get; set; } = string.Empty;
}

public sealed class ClientBox
{
    [JsonPropertyName("id")]       public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")]     public string Name { get; set; } = string.Empty;
    [JsonPropertyName("online")]   public bool Online { get; set; }
    [JsonPropertyName("lastSeen")] public long LastSeen { get; set; }
    [JsonPropertyName("report")]   public ClientReport? Report { get; set; }
}

public sealed class ClientReport
{
    [JsonPropertyName("state")]        public string State { get; set; } = "idle";
    [JsonPropertyName("role")]         public string Role { get; set; } = string.Empty;
    [JsonPropertyName("territory")]    public uint Territory { get; set; }
    [JsonPropertyName("target")]       public uint Target { get; set; }
    [JsonPropertyName("round")]        public int Round { get; set; }
    [JsonPropertyName("party")]        public List<string> Party { get; set; } = [];
    [JsonPropertyName("penaltyUntil")] public long PenaltyUntil { get; set; }
    [JsonPropertyName("queueStartAt")] public long QueueStartAt { get; set; }
    [JsonPropertyName("at")]           public long At { get; set; }
}

public sealed class Command
{
    [JsonPropertyName("seq")]     public long Seq { get; set; }
    [JsonPropertyName("type")]    public string Type { get; set; } = string.Empty;
    [JsonPropertyName("target")]  public string? Target { get; set; }
    [JsonPropertyName("startAt")] public long StartAt { get; set; }
    [JsonPropertyName("stagger")] public Stagger? Stagger { get; set; }
    [JsonPropertyName("round")]   public int Round { get; set; }
    [JsonPropertyName("at")]      public long At { get; set; }
}

public sealed class CreateRoomResponse
{
    [JsonPropertyName("code")]      public string Code { get; set; } = string.Empty;
    [JsonPropertyName("hostToken")] public string HostToken { get; set; } = string.Empty;
}
