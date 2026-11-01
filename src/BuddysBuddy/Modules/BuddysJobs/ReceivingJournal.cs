using System;
using System.IO;
using BuddysBuddy.Services;
using Newtonsoft.Json;

namespace BuddysBuddy.Modules.BuddysJobs;

internal sealed class ReceivingJournal
{
    public string JobId { get; set; } = "";
    public string From { get; set; } = "";
    public string Owner { get; set; } = "";
    public int Received { get; set; }
    public long Paid { get; set; }
    public long StillOwed { get; set; }
    public int PendingUnits { get; set; }
    public long PendingGil { get; set; }
    private static string Path => System.IO.Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, "jobs-receiving-pending.json");
    public static bool Exists => File.Exists(Path);
    public static string Summary()
    {
        var record = JsonConvert.DeserializeObject<ReceivingJournal>(File.ReadAllText(Path));
        return record is null ? "The saved receiving record could not be read." :
            $"{record.Owner} received {record.Received:N0} units from {record.From} and paid {record.Paid:N0} gil. " +
            $"Outstanding payment: {record.StillOwed:N0} gil. " +
            $"The last unverified trade contained {record.PendingUnits:N0} units and {record.PendingGil:N0} gil. Job: {record.JobId}.";
    }
    public void Save(bool create = false)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (create)
        {
            using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        else
        {
            File.WriteAllText(Path + ".tmp", JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(Path + ".tmp", Path, true);
        }
    }
    public static void Clear() { if (Exists) File.Delete(Path); }
}
