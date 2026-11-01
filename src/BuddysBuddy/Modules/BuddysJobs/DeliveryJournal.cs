using System;
using System.IO;
using BuddysBuddy.Services;
using Newtonsoft.Json;

namespace BuddysBuddy.Modules.BuddysJobs;

/// <summary>Survives unloads and uncertain board replies. Never retries a transfer or POST.</summary>
internal sealed class DeliveryJournal
{
    public string JobId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Item { get; set; } = "";
    public int Verified { get; set; }
    public int InFlight { get; set; }
    public long GilStillDue { get; set; }
    public long InFlightGil { get; set; }
    public string Character { get; set; } = "";
    public string Server { get; set; } = "";
    private static string Path => System.IO.Path.Combine(
        DalamudServices.PluginInterface.ConfigDirectory.FullName, "jobs-delivery-pending.json");
    public static bool Exists => File.Exists(Path);
    public static string Summary()
    {
        if (!Exists) return "";
        try
        {
            var record = JsonConvert.DeserializeObject<DeliveryJournal>(File.ReadAllText(Path));
            return record is null ? "An earlier delivery needs review." :
                $"Review the earlier delivery by {record.Character} to {record.Recipient}: " +
                $"{record.Verified:N0}x {record.Item} verified traded, {record.InFlight:N0} in the last unverified trade. " +
                $"Payment still due: {record.GilStillDue:N0} gil; last trade expected {record.InFlightGil:N0} gil. " +
                $"Check the actual trade and board ({record.Server}) before clearing this notice. Job: {record.JobId}.";
        }
        catch (Exception) { return "The saved delivery record could not be read. Review jobs-delivery-pending.json and your last trade before clearing it."; }
    }
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path + ".tmp", JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(Path + ".tmp", Path, true);
    }
    public void Create()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonConvert.SerializeObject(this, Formatting.Indented));
    }
    public static void Clear() { if (Exists) File.Delete(Path); }
}
