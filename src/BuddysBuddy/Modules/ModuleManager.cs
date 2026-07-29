using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BuddysBuddy.Config;
using BuddysBuddy.Services;

namespace BuddysBuddy.Modules;

/// <summary>
/// Discovers, loads and unloads modules.
/// </summary>
/// <remarks>
/// Every call into module code is wrapped so one misbehaving module cannot take down the host or
/// the rest of the modules. A module that throws during <see cref="IModule.Load"/> is recorded in
/// <see cref="Failures"/>, left disabled, and surfaced in the settings window.
/// </remarks>
public sealed class ModuleManager : IDisposable
{
    private readonly Configuration _config;
    private readonly List<IModule> _modules = new();
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    public ModuleManager(Configuration config) => _config = config;

    /// <summary>All discovered modules, in display order.</summary>
    public IReadOnlyList<IModule> Modules => _modules;

    /// <summary>Modules currently loaded, in display order.</summary>
    public IEnumerable<IModule> LoadedModules => _modules.Where(m => m.IsLoaded);

    /// <summary>Load failures keyed by module internal name.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>
    /// Reflects over this assembly for <see cref="IModule"/> implementations, instantiates each one,
    /// and loads those the config says are enabled.
    /// </summary>
    public void DiscoverAndLoad()
    {
        var types = Assembly.GetExecutingAssembly()
                            .GetTypes()
                            .Where(t => typeof(IModule).IsAssignableFrom(t)
                                        && t is { IsAbstract: false, IsInterface: false }
                                        && t.GetConstructor(Type.EmptyTypes) != null);

        foreach (var type in types)
        {
            try
            {
                var module = (IModule)Activator.CreateInstance(type)!;

                if (_modules.Any(m => string.Equals(m.InternalName, module.InternalName, StringComparison.OrdinalIgnoreCase)))
                {
                    DalamudServices.Log.Error(
                        "Duplicate module InternalName '{Name}' on {Type} — skipping.", module.InternalName, type.FullName!);
                    module.Dispose();
                    continue;
                }

                _modules.Add(module);
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(ex, "Failed to construct module {Type}.", type.FullName!);
                _failures[type.Name] = ex.Message;
            }
        }

        _modules.Sort(static (a, b) =>
        {
            var byOrder = a.Order.CompareTo(b.Order);
            return byOrder != 0 ? byOrder : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        DalamudServices.Log.Information("Discovered {Count} module(s).", _modules.Count);

        foreach (var module in _modules)
        {
            if (_config.IsEnabled(module.InternalName, module.EnabledByDefault))
                TryLoad(module);
        }
    }

    public IModule? Find(string internalName)
        => _modules.FirstOrDefault(m => string.Equals(m.InternalName, internalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Enables or disables a module and persists the choice. Takes effect immediately.</summary>
    public void SetEnabled(IModule module, bool enabled)
    {
        _config.SetEnabled(module.InternalName, enabled);

        if (enabled)
            TryLoad(module);
        else
            TryUnload(module);
    }

    /// <summary>Loads a module, capturing any failure instead of letting it escape.</summary>
    public bool TryLoad(IModule module)
    {
        try
        {
            module.Load();
            _failures.Remove(module.InternalName);
            DalamudServices.Log.Debug("Loaded module {Name}.", module.InternalName);
            return true;
        }
        catch (Exception ex)
        {
            _failures[module.InternalName] = ex.Message;
            DalamudServices.Log.Error(ex, "Module {Name} failed to load; leaving it disabled.", module.InternalName);

            // Best-effort cleanup of anything a partial Load left behind.
            TryUnload(module);
            return false;
        }
    }

    public void TryUnload(IModule module)
    {
        try
        {
            module.Unload();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Module {Name} threw while unloading.", module.InternalName);
        }
    }

    /// <summary>Routes <c>/bb &lt;module&gt; [args]</c> to the matching loaded module.</summary>
    public bool TryHandleCommand(string moduleName, string args, out IModule? module)
    {
        module = Find(moduleName);
        if (module is null || !module.IsLoaded)
            return false;

        try
        {
            return module.HandleCommand(args);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Module {Name} threw handling a command.", module.InternalName);
            return true;
        }
    }

    /// <summary>Safely draws a module's pane, reporting rather than propagating a draw exception.</summary>
    public void DrawModule(IModule module, Action<Exception> onError)
    {
        try
        {
            module.Draw();
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    public void Dispose()
    {
        // Reverse order so modules that registered later tear down first.
        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            try
            {
                _modules[i].Dispose();
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(ex, "Module {Name} threw while disposing.", _modules[i].InternalName);
            }
        }

        _modules.Clear();
    }
}
