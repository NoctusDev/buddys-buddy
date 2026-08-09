#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2075;IL3000

using System.Reflection;
using System.Text.Json;

// Static verification of a built Dalamud plugin. Confirms the manifest's API level matches the
// installed Dalamud and that the entry point and modules are discoverable by reflection — the
// structural checks the game performs at load time, without launching the game.
//
//   dotnet run scripts/verify.cs                                 (BuddysBuddy, Debug)
//   dotnet run scripts/verify.cs -- . Release
//
// Exists because GitHub Actions cannot run on this account (see knowledge/verified.md), so this
// is the substitute for CI. Uses a reflection-only load, so nothing in the plugin is executed.

var repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var config = args.Length > 1 ? args[1] : "Debug";

// Which plugin to check. The repo holds more than one, and each has its own bin folder and its
// own emitted API level — checking only the first one is how a stale sibling ships.
var plugin = args.Length > 2 ? args[2] : "BuddysBuddy";

var binDir = Path.Combine(repo, "src", plugin, "bin", config);
var dll = Path.Combine(binDir, $"{plugin}.dll");
var manifestPath = Path.Combine(binDir, $"{plugin}.json");

Console.WriteLine($"Plugin:  {plugin} ({config})");

// Resolve the Dalamud this machine actually runs. The SDK default (%AppData%\XIVLauncher) is NOT
// it here — XIVLauncher runs with --roamingPath, and the default location holds a stale install on
// an older API level. Checking against the wrong one is how an "outdated" plugin passed verify.
// Keep this in sync with Directory.Build.props.
var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

var dalamudDir =
    Environment.GetEnvironmentVariable("DALAMUD_HOME") is { Length: > 0 } env && Directory.Exists(env)
        ? env
        : Directory.Exists(@"d:\Multibox\Glumishxiv3\addon\Hooks\dev")
            ? @"d:\Multibox\Glumishxiv3\addon\Hooks\dev"
            : Path.Combine(appData, "XIVLauncher", "addon", "Hooks", "dev");

// installedPlugins sits beside addon/, three levels above the Hooks\dev folder.
var roamingRoot = Path.GetFullPath(Path.Combine(dalamudDir, "..", "..", ".."));

Console.WriteLine($"Dalamud: {dalamudDir}\n");

var failures = new List<string>();
void Check(bool ok, string label, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures.Add(label);
}

Console.WriteLine("Artifacts");
Check(File.Exists(dll), "plugin DLL exists", dll);
Check(File.Exists(manifestPath), "manifest sits beside the DLL", Path.GetFileName(manifestPath));
if (failures.Count > 0)
{
    Console.WriteLine($"\nBuild first:  dotnet build BuddysBuddy.slnx -c {config}");
    return 1;
}

// The API level Dalamud accepts, inferred from the plugins it currently has installed. This is
// the check that matters most: a mismatch builds cleanly but is rejected at load time.
Console.WriteLine("\nAPI level");
var installedRoot = Path.Combine(roamingRoot, "installedPlugins");
var levels = new Dictionary<int, int>();

if (Directory.Exists(installedRoot))
{
    foreach (var f in Directory.EnumerateFiles(installedRoot, "*.json", SearchOption.AllDirectories))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            if (doc.RootElement.TryGetProperty("DalamudApiLevel", out var v) && v.TryGetInt32(out var lvl))
                levels[lvl] = levels.GetValueOrDefault(lvl) + 1;
        }
        catch
        {
            // Not a plugin manifest — ignore.
        }
    }
}

using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
var ours = manifestDoc.RootElement.GetProperty("DalamudApiLevel").GetInt32();

if (levels.Count == 0)
{
    Console.WriteLine($"  SKIP  no installed plugins to compare against (ours is {ours})");
}
else
{
    // Majority wins: a stale disabled plugin on an old API level should not skew the answer.
    var expected = levels.MaxBy(kv => kv.Value).Key;
    var tally = string.Join(", ", levels.OrderBy(kv => kv.Key).Select(kv => $"API {kv.Key}x{kv.Value}"));
    Check(ours == expected, "manifest API level matches installed Dalamud", $"ours={ours}, installed={tally}");
}

Console.WriteLine("\nAssembly");
var resolverPaths = new List<string> { dll };
resolverPaths.AddRange(Directory.GetFiles(dalamudDir, "*.dll"));
resolverPaths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths.Distinct()));
var asm = mlc.LoadFromAssemblyPath(dll);
var types = asm.GetTypes();

static bool Implements(Type t, string iface) => t.GetInterfaces().Any(i => i.FullName == iface);

var entry = types.Where(t => Implements(t, "Dalamud.Plugin.IDalamudPlugin") && !t.IsAbstract).ToList();
Check(entry.Count == 1, "exactly one IDalamudPlugin entry point",
    entry.Count == 0 ? "none found" : string.Join(", ", entry.Select(t => t.Name)));

if (entry.Count == 1)
{
    var ctors = entry[0].GetConstructors();
    Check(ctors.Length == 1, "entry point has a single public constructor",
        string.Join(" | ", ctors.Select(c =>
            "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")")));
}

Console.WriteLine("\nModules");
var moduleIface = $"{asm.GetName().Name}.Modules.IModule";
var modules = types.Where(t => Implements(t, moduleIface) && !t.IsAbstract && !t.IsInterface).ToList();

Check(modules.Count > 0, "at least one module discoverable", $"{modules.Count} found");

// ModuleManager instantiates by reflection, and duplicate internal names collide in config.
foreach (var m in modules)
{
    Check(m.GetConstructor(Type.EmptyTypes) != null,
        $"{m.Name} has a public parameterless constructor",
        "required by reflection discovery");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All checks passed. The plugin is structurally loadable by Dalamud.");
    Console.WriteLine($"Dev Plugin Location:  {dll}");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed:");
foreach (var f in failures)
    Console.WriteLine($"  - {f}");
return 1;
