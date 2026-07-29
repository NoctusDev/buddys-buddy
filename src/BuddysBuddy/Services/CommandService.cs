using System;
using System.Linq;
using System.Text;
using BuddysBuddy.Modules;
using Dalamud.Game.Command;

namespace BuddysBuddy.Services;

/// <summary>
/// Owns the plugin's chat commands.
/// </summary>
/// <remarks>
/// Only <c>/bb</c> (and the <c>/buddysbuddy</c> alias) are registered globally; each module is
/// reached as <c>/bb &lt;module&gt; [args]</c>. That keeps Dalamud's shared command namespace clean
/// however many modules get added, and means a module needs no command registration of its own.
/// </remarks>
public sealed class CommandService : IDisposable
{
    private const string Command = "/bb";
    private const string Alias = "/buddysbuddy";

    private readonly ModuleManager _modules;
    private readonly Action _toggleMain;
    private readonly Action _toggleSettings;

    public CommandService(ModuleManager modules, Action toggleMain, Action toggleSettings)
    {
        _modules = modules;
        _toggleMain = toggleMain;
        _toggleSettings = toggleSettings;

        DalamudServices.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Buddy's Buddy window. \"/bb help\" lists module commands.",
        });

        DalamudServices.CommandManager.AddHandler(Alias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /bb.",
            ShowInHelp = false,
        });
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();

        if (args.Length == 0)
        {
            _toggleMain();
            return;
        }

        var split = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = split[0];
        var rest = split.Length > 1 ? split[1].Trim() : string.Empty;

        switch (verb.ToLowerInvariant())
        {
            case "settings":
            case "config":
                _toggleSettings();
                return;

            case "help":
            case "?":
                PrintHelp();
                return;
        }

        var module = _modules.Find(verb);
        if (module is null)
        {
            DalamudServices.ChatGui.PrintError($"[Buddy's Buddy] Unknown command \"{verb}\". Try /bb help.");
            return;
        }

        if (!module.IsLoaded)
        {
            DalamudServices.ChatGui.PrintError($"[Buddy's Buddy] Module \"{module.Name}\" is disabled.");
            return;
        }

        if (!_modules.TryHandleCommand(verb, rest, out _))
            DalamudServices.ChatGui.PrintError($"[Buddy's Buddy] \"{module.Name}\" did not understand \"{rest}\".");
    }

    private void PrintHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("/bb — toggle the main window");
        sb.AppendLine("/bb settings — open settings");

        var loaded = _modules.LoadedModules.ToList();
        if (loaded.Count == 0)
        {
            sb.Append("No modules are enabled.");
        }
        else
        {
            sb.AppendLine("Modules:");
            foreach (var module in loaded)
                sb.AppendLine($"  /bb {module.InternalName} — {module.Name}");
        }

        DalamudServices.ChatGui.Print(sb.ToString().TrimEnd());
    }

    public void Dispose()
    {
        DalamudServices.CommandManager.RemoveHandler(Command);
        DalamudServices.CommandManager.RemoveHandler(Alias);
    }
}
