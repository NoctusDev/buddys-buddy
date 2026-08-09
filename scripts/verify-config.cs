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

// The remembered item sources are the only thing this plugin persists outside the plugin config.
// They are their own file precisely so a break here cannot kill the plugin — but a break here is
// still silent, so round-trip it the same way.
Console.WriteLine("\nRemembered item sources");
try
{
    var storeType = plugin.GetType("BuddysBuddy.Game.SourceStore")!;
    var snapshotType = plugin.GetType("BuddysBuddy.Game.SnapshotSource")!;
    var keyType = plugin.GetType("BuddysBuddy.Game.ItemKey")!;
    var sourceType = plugin.GetType("BuddysBuddy.Game.IItemSource")!;

    var items = (System.Collections.IDictionary)Activator.CreateInstance(
        typeof(Dictionary<,>).MakeGenericType(keyType, typeof(int)))!;
    items[Activator.CreateInstance(keyType, [(uint)5111, true])!] = 640;
    items[Activator.CreateInstance(keyType, [(uint)5112, false])!] = 12;

    var source = Activator.CreateInstance(snapshotType, ["retainer:DEAD", "Chocobo", "Glumish · retainers"])!;
    snapshotType.GetMethod("Update")!.Invoke(source, [items, 1_234_567L]);

    var list = (System.Collections.IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(sourceType))!;
    list.Add(source);

    var json = (string)storeType.GetMethod("Serialize")!.Invoke(null, [list])!;
    var back = (System.Collections.IList)storeType.GetMethod("Deserialize")!.Invoke(null, [json])!;

    Check(back.Count == 1, "a saved source reads back", $"{back.Count} source(s)");

    var restored = back[0]!;
    var gil = (long)snapshotType.GetProperty("Gil")!.GetValue(restored)!;
    var live = (bool)snapshotType.GetProperty("IsLive")!.GetValue(restored)!;
    var restoredItems = (System.Collections.ICollection)snapshotType.GetProperty("Items")!.GetValue(restored)!;

    Check(gil == 1_234_567L, "gil survives the round-trip", $"{gil:N0}");
    Check(restoredItems.Count == 2, "items survive the round-trip", $"{restoredItems.Count} entry/entries");
    Check(!live, "a restored source is not reported as live", "stale counts must never look current");
}
catch (TargetInvocationException ex)
{
    Check(false, "item source round-trip", ex.InnerException?.Message ?? ex.Message);
}

// The desynthesis ledger is the other thing kept outside the plugin config, and the one that is
// genuinely irreplaceable — it is the record of hundreds of destroyed items. Round-trip it too.
Console.WriteLine("\nDesynthesis ledger");
try
{
    var ledgerType = plugin.GetType("BuddysBuddy.Modules.BuddysDesynther.DesynthLedger")!;
    var entryType = plugin.GetType("BuddysBuddy.Modules.BuddysDesynther.LedgerEntry")!;

    var entry = Activator.CreateInstance(entryType)!;
    entryType.GetProperty("SourceItemId")!.SetValue(entry, (uint)6183);
    entryType.GetProperty("Runs")!.SetValue(entry, 37);
    entryType.GetProperty("Disagreements")!.SetValue(entry, 1);
    entryType.GetProperty("FirstSeen")!.SetValue(entry, DateTime.Now.AddDays(-3));
    entryType.GetProperty("LastSeen")!.SetValue(entry, DateTime.Now);

    var yield = (System.Collections.IDictionary)entryType.GetProperty("Yield")!.GetValue(entry)!;
    yield["5111"] = 74L;
    yield["5112h"] = 3L;

    var entries = (System.Collections.IList)Activator.CreateInstance(
        typeof(List<>).MakeGenericType(entryType))!;
    entries.Add(entry);

    var json = (string)ledgerType.GetMethod("Serialize")!.Invoke(null, [entries])!;
    var back = (System.Collections.IList)ledgerType.GetMethod("Deserialize")!.Invoke(null, [json])!;

    Check(back.Count == 1, "a ledger entry reads back", $"{back.Count} entry/entries");

    var restored = back[0]!;
    var runs = (int)entryType.GetProperty("Runs")!.GetValue(restored)!;
    var restoredYield = (System.Collections.IDictionary)entryType.GetProperty("Yield")!.GetValue(restored)!;

    Check(runs == 37, "the run count survives", $"{runs} run(s)");
    Check(restoredYield.Count == 2, "yields survive", $"{restoredYield.Count} result item(s)");

    // PerRun is what the whole module's verdict column is computed from.
    var perRun = (double)entryType.GetMethod("PerRun")!.Invoke(restored, ["5111"])!;
    Check(Math.Abs(perRun - 74.0 / 37.0) < 0.0001, "the per-run average is right", $"{perRun:F3}");

    // Round-tripping HQ yields through a string key is the easiest thing here to get wrong.
    var key = ledgerType.GetMethod("FromKey")!.Invoke(null, ["5112h"])!;
    var isHq = (bool)key.GetType().GetProperty("HighQuality")!.GetValue(key)!;
    Check(isHq, "an HQ yield key round-trips as HQ", "5112h");
}
catch (TargetInvocationException ex)
{
    Check(false, "ledger round-trip", ex.InnerException?.Message ?? ex.Message);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "Config round-trip OK." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
