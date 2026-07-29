using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BuddysBuddy.Services;

/// <summary>
/// Dalamud services, injected once at startup via <c>PluginInterface.Create&lt;DalamudServices&gt;()</c>.
/// </summary>
/// <remarks>
/// Modules read these directly rather than taking constructor parameters, which keeps module code
/// short. Add new services here as modules need them — unused ones cost nothing.
/// </remarks>
public sealed class DalamudServices
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static INotificationManager Notifications { get; private set; } = null!;
}
