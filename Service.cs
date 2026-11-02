namespace MentorRoulette;

using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

public sealed class Service
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IPluginLog             Log             { get; private set; } = null!;
    [PluginService] public static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] public static IDutyState             DutyState       { get; private set; } = null!;
    [PluginService] public static IDataManager           Data            { get; private set; } = null!;
    [PluginService] public static IFramework             Framework       { get; private set; } = null!;
    [PluginService] public static IChatGui               Chat            { get; private set; } = null!;
    [PluginService] public static ICommandManager        Commands        { get; private set; } = null!;
    [PluginService] public static IGameGui               GameGui         { get; private set; } = null!;
    [PluginService] public static ICondition             Condition       { get; private set; } = null!;
    [PluginService] public static IObjectTable           Objects         { get; private set; } = null!;
    [PluginService] public static IPartyList             PartyList       { get; private set; } = null!;
    [PluginService] public static ISigScanner            Sig             { get; private set; } = null!;
}
