namespace MentorRoulette.Sync;

/// <summary>
/// 默认中继服务器地址。用户不通过 UI 修改，只能改配置文件里的 SyncServerUrl。
/// </summary>
internal static class SyncDefaults
{
    /// <summary>默认中继服务器（阿里云 ECS，nginx 反代到 /mr/）。</summary>
    internal const string ServerUrl = "http://121.40.159.61/mr";
}
