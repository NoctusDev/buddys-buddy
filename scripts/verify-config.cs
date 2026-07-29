#:property NoWarn=IL2026;IL2060;IL2070;IL2072;IL2075;IL3050

using System.Reflection;

// Round-trips the plugin's Configuration through Newtonsoft with the same TypeNameHandling Dalamud
// applies, against the real config file on disk.
//
//   dotnet run scripts/verify-config.cs
//
// Exists because a config Dalamud cannot deserialise throws out of the plugin constructor and the
// plugin fails to load entirely — with the only clue buried in dalamud.log. That happened once:
// a Dictionary<string, IModuleConfig> saved cleanly, then could not be read back, because Dalamud
// writes $type discriminators but does not honour them for nested values on load.
//
// scripts/verify.cs cannot catch this: it uses a reflection-only load and never runs a serialiser.

var dalamudDir =
    Environment.GetEnvironmentVariable("DALAMUD_HOME") is { Length: > 0 } env && Directory.Exists(env)
        ? env
        : Directory.Exists(@"d:\Multibox\Glumishxiv3\addon\Hooks\dev")
            ? @"d:\Multibox\Glumishxiv3\addon\Hooks\dev"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "XIVLauncher", "addon", "Hooks", "dev");

var roamingRoot = Path.GetFullPath(Path.Combine(dalamudDir, "..", "..", ".."));
var repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var pluginDll = Path.Combine(repo, "src", "BuddysBuddy", "bin", "Debug", "BuddysBuddy.dll");
var configFile = Path.Combine(roamingRoot, "pluginConfigs", "BuddysBuddy.json");

if (!File.Exists(pluginDll))
{
    Console.WriteLine($"Build first: dotnet build BuddysBuddy.slnx\n  missing {pluginDll}");
    return 1;
}

// Serve every dependency out of the live Dalamud folder so assembly identities match exactly.
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var path = Path.Combine(dalamudDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};

var plugin = Assembly.LoadFrom(pluginDll);
var newtonsoft = Assembly.LoadFrom(Path.Combine(dalamudDir, "Newtonsoft.Json.dll"));

var configType = plugin.GetType("BuddysBuddy.Config.Configuration")!;
var jsonConvert = newtonsoft.GetType("Newtonsoft.Json.JsonConvert")!;
var settingsType = newtonsoft.GetType("Newtonsoft.Json.JsonSerializerSettings")!;
var handlingType = newtonsoft.GetType("Newtonsoft.Json.TypeNameHandling")!;

// TypeNameHandling.Objects — what Dalamud uses when it writes plugin configs.
var settings = Activator.CreateInstance(settingsType)!;
settingsType.GetProperty("TypeNameHandling")!.SetValue(settings, Enum.Parse(handlingType, "Objects"));

var deserialize = jsonConvert.GetMethod("DeserializeObject", [typeof(string), typeof(Type), settingsType])!;
var serialize = jsonConvert.GetMethod("SerializeObject", [typeof(object), typeof(Type), settingsType])!;

var failures = 0;
void Check(bool ok, string label, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures++;
}

Console.WriteLine($"Config: {configFile}\n");
Console.WriteLine("Saved config on disk");

if (File.Exists(configFile))
{
    var saved = File.ReadAllText(configFile);
    if (saved.Contains("\"ModuleConfigs\""))
        Console.WriteLine("  info  file still carries the pre-fix ModuleConfigs key; it should be ignored");

    try
    {
        Check(deserialize.Invoke(null, [saved, configType, settings]) is not null,
            "the real saved config deserialises");
    }
    catch (TargetInvocationException ex)
    {
        Check(false, "the real saved config deserialises", ex.InnerException?.Message ?? ex.Message);
    }
}
else
{
    Console.WriteLine("  SKIP  no config saved yet");
}

Console.WriteLine("\nRound-trip with module settings populated");
try
{
    var fresh = Activator.CreateInstance(configType)!;
    var jobject = newtonsoft.GetType("Newtonsoft.Json.Linq.JObject")!;
    var blob = jobject.GetMethod("Parse", [typeof(string)])!
        .Invoke(null, ["""{"QueueMode":true,"LastAmount":42}"""])!;

    var dict = configType.GetProperty("ModuleSettings")!.GetValue(fresh)!;
    dict.GetType().GetMethod("set_Item")!.Invoke(dict, ["slaves", blob]);

    var json = (string)serialize.Invoke(null, [fresh, configType, settings])!;
    var back = deserialize.Invoke(null, [json, configType, settings]);
    Check(back is not null, "save then load survives a populated module config");

    var restored = configType.GetProperty("ModuleSettings")!.GetValue(back!)!;
    var count = (int)restored.GetType().GetProperty("Count")!.GetValue(restored)!;
    Check(count == 1, "module settings survive the round-trip", $"{count} entry/entries");

    var slaves = configType.GetMethod("GetModuleConfig")!
        .MakeGenericMethod(plugin.GetType("BuddysBuddy.Modules.BuddysSlaves.BuddysSlavesConfig")!)
        .Invoke(back, ["slaves"])!;

    var amount = (int)slaves.GetType().GetProperty("LastAmount")!.GetValue(slaves)!;
    var queue = (bool)slaves.GetType().GetProperty("QueueMode")!.GetValue(slaves)!;
    Check(amount == 42 && queue, "values read back correctly", $"LastAmount={amount}, QueueMode={queue}");
}
catch (TargetInvocationException ex)
{
    Check(false, "round-trip", ex.InnerException?.Message ?? ex.Message);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "Config round-trip OK." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
