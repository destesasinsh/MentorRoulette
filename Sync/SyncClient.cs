namespace MentorRoulette.Sync;

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>与中继服务器通信（HTTP/JSON）。</summary>
internal sealed class SyncClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };

    internal string ServerUrl { get; private set; } = string.Empty;
    internal string LastError { get; private set; } = string.Empty;
    internal DateTime LastOkAt { get; private set; } = DateTime.MinValue;

    internal bool Connected => (DateTime.UtcNow - this.LastOkAt).TotalSeconds < 10;

    internal void SetServer(string url) =>
        this.ServerUrl = (url ?? string.Empty).Trim().TrimEnd('/');

    private async Task<T?> CallAsync<T>(HttpMethod method, string path, object? body)
    {
        if (this.ServerUrl.Length == 0)
            return default;

        try
        {
            using HttpRequestMessage req = new(method, this.ServerUrl + path);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

            using HttpResponseMessage res = await this.http.SendAsync(req).ConfigureAwait(false);
            string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                this.LastError = $"{(int)res.StatusCode} {text}";
                return default;
            }

            this.LastError  = string.Empty;
            this.LastOkAt   = DateTime.UtcNow;

            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            return default;
        }
    }

    internal Task<CreateRoomResponse?> CreateRoomAsync(string name) =>
        this.CallAsync<CreateRoomResponse>(HttpMethod.Post, "/room/create", new { name });

    internal Task<RoomState?> JoinAsync(string code, string clientId, string name) =>
        this.CallAsync<RoomState>(HttpMethod.Post, $"/room/{code}/join", new { clientId, name });

    internal Task<RoomState?> PollAsync(string code, string clientId) =>
        this.CallAsync<RoomState>(HttpMethod.Get,
                                  $"/room/{code}/state?since=0&seq=0&clientId={Uri.EscapeDataString(clientId)}",
                                  null);

    internal Task<object?> ReportAsync(string code, string clientId, ClientReport report) =>
        this.CallAsync<object>(HttpMethod.Post, $"/room/{code}/report", new
        {
            clientId,
            state        = report.State,
            role         = report.Role,
            territory    = report.Territory,
            target       = report.Target,
            round        = report.Round,
            party        = report.Party,
            penaltyUntil = report.PenaltyUntil,
            queueStartAt = report.QueueStartAt,
        });

    internal Task<object?> ConfigAsync(string code, string hostToken, object assignments, RoomOptions options, Stagger stagger) =>
        this.CallAsync<object>(HttpMethod.Post, $"/room/{code}/config", new { hostToken, assignments, options, stagger });

    internal Task<object?> SignalAsync(string code, string hostToken, string type, string? target = null, long? startAt = null) =>
        this.CallAsync<object>(HttpMethod.Post, $"/room/{code}/signal", new { hostToken, type, target, startAt });

    internal Task<object?> LeaveAsync(string code, string clientId) =>
        this.CallAsync<object>(HttpMethod.Post, $"/room/{code}/leave", new { clientId });

    public void Dispose() => this.http.Dispose();
}
