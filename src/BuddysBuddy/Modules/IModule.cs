using System;

namespace BuddysBuddy.Modules;

/// <summary>
/// A self-contained feature hosted by Buddy's Buddy — effectively a mini-plugin.
/// </summary>
/// <remarks>
/// Every non-abstract implementation in this assembly is discovered automatically at startup by
/// <see cref="ModuleManager"/>; there is no registration list to update. Implementations need a
/// public parameterless constructor, and should do nothing expensive in it — real setup belongs in
/// <see cref="Load"/>, which only runs when the module is enabled.
///
/// Prefer deriving from <see cref="ModuleBase"/> rather than implementing this directly.
/// </remarks>
public interface IModule : IDisposable
{
    /// <summary>Display name, shown in the module list.</summary>
    string Name { get; }

    /// <summary>
    /// Stable identifier used as the config key and the <c>/bb &lt;subcommand&gt;</c> token.
    /// Never change this after release — doing so orphans the module's saved settings.
    /// </summary>
    string InternalName { get; }

    /// <summary>One-line summary shown under the module name.</summary>
    string Description { get; }

    /// <summary>Whether this module is enabled by default the first time it is seen.</summary>
    bool EnabledByDefault { get; }

    /// <summary>Sort key for the module list; ties break alphabetically by <see cref="Name"/>.</summary>
    int Order { get; }

    /// <summary>True between a successful <see cref="Load"/> and the matching <see cref="Unload"/>.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Acquire resources: subscribe to framework/game events, register hooks, register commands.
    /// Called when the module is enabled — at startup or later from the settings window.
    /// </summary>
    void Load();

    /// <summary>
    /// Release everything <see cref="Load"/> acquired. Must be safe to call when not loaded, and
    /// must leave the module in a state where <see cref="Load"/> can run again.
    /// </summary>
    void Unload();

    /// <summary>Draws the module's main pane in the host window. Only called while loaded.</summary>
    void Draw();

    /// <summary>Draws the module's settings under its entry in the settings window.</summary>
    void DrawSettings();

    /// <summary>
    /// Handles <c>/bb &lt;InternalName&gt; [args]</c>. Return false to let the host print the
    /// default "unknown arguments" help for this module.
    /// </summary>
    bool HandleCommand(string args);
}
