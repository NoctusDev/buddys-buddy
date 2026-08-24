using System;
using Newtonsoft.Json;

namespace BuddysBuddy.Modules.BuddysJobs;

/// <summary>Where a job has got to.</summary>
public enum JobState
{
    /// <summary>Nobody has taken it. Anyone can.</summary>
    Open,

    /// <summary>Someone is working on it.</summary>
    Claimed,

    /// <summary>The full quantity has been delivered.</summary>
    Done,

    /// <summary>Called off by whoever posted it.</summary>
    Cancelled,

    /// <summary>A state this build does not know about. Shown, never acted on.</summary>
    Unknown,
}

/// <summary>
/// One request on the board: somebody wants a quantity of an item.
/// </summary>
/// <remarks>
/// The wire shape, deserialised as it arrives. Nothing here is authoritative locally — the server
/// owns every one of these fields, and a local edit would only be overwritten by the next poll.
/// </remarks>
public sealed class Job
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("itemId")]
    public uint ItemId { get; set; }

    /// <summary>
    /// The item's name as the poster's client resolved it.
    /// </summary>
    /// <remarks>
    /// Carried so a job still reads sensibly on a client whose item sheet cannot resolve the id —
    /// a different game language, or a version behind. The local sheet is preferred when it answers.
    /// </remarks>
    [JsonProperty("itemName")]
    public string ItemName { get; set; } = string.Empty;

    [JsonProperty("quantity")]
    public int Quantity { get; set; }

    [JsonProperty("delivered")]
    public int Delivered { get; set; }

    [JsonProperty("hq")]
    public bool HighQuality { get; set; }

    [JsonProperty("reward")]
    public string Reward { get; set; } = string.Empty;

    [JsonProperty("note")]
    public string Note { get; set; } = string.Empty;

    [JsonProperty("state")]
    public string StateName { get; set; } = string.Empty;

    [JsonProperty("postedBy")]
    public string PostedBy { get; set; } = string.Empty;

    [JsonProperty("claimedBy")]
    public string ClaimedBy { get; set; } = string.Empty;

    [JsonProperty("claimedAt")]
    public long ClaimedAtUnix { get; set; }

    [JsonProperty("createdAt")]
    public long CreatedAtUnix { get; set; }

    [JsonProperty("updatedAt")]
    public long UpdatedAtUnix { get; set; }

    /// <summary>
    /// The state as an enum.
    /// </summary>
    /// <remarks>
    /// A state this build has never heard of maps to <see cref="JobState.Unknown"/> rather than
    /// throwing or silently reading as <see cref="JobState.Open"/> — a newer server adding one must
    /// not make an old client offer to claim something it does not understand.
    /// </remarks>
    [JsonIgnore]
    public JobState State => StateName.ToLowerInvariant() switch
    {
        "open" => JobState.Open,
        "claimed" => JobState.Claimed,
        "done" => JobState.Done,
        "cancelled" => JobState.Cancelled,
        _ => JobState.Unknown,
    };

    [JsonIgnore]
    public int Remaining => Math.Max(0, Quantity - Delivered);

    [JsonIgnore]
    public bool IsFinished => State is JobState.Done or JobState.Cancelled;

    [JsonIgnore]
    public DateTime Created => DateTimeOffset.FromUnixTimeSeconds(CreatedAtUnix).LocalDateTime;

    [JsonIgnore]
    public DateTime Updated => DateTimeOffset.FromUnixTimeSeconds(UpdatedAtUnix).LocalDateTime;
}

/// <summary>What the board looked like the last time it was successfully read.</summary>
/// <param name="Jobs">Every job the board returned, newest change first.</param>
/// <param name="LastSync">When that read succeeded, or null if none ever has.</param>
/// <param name="LastError">Why the last attempt failed, or null if it did not.</param>
/// <param name="Busy">True while a request is in flight.</param>
internal sealed record BoardState(
    System.Collections.Generic.IReadOnlyList<Job> Jobs,
    DateTime? LastSync,
    string? LastError,
    bool Busy)
{
    public static readonly BoardState Empty = new(Array.Empty<Job>(), null, null, false);
}
