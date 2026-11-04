using FFXIVClientStructs.FFXIV.Client.Game;

namespace VenueAudio;

/// <summary>The house the player is standing in. Read on the framework thread only.</summary>
public sealed record VenueLocation(string Key, string Label)
{
    public static unsafe VenueLocation? Current()
    {
        var manager = HousingManager.Instance();
        if (manager == null || !manager->IsInside()) return null;
        // HouseId.Id packs world, district, ward, plot and room, so it names one venue exactly.
        var house = manager->GetCurrentIndoorHouseId();
        if (house.Id is 0 or ulong.MaxValue) return null;
        string label = house.IsApartment
            ? $"Ward {house.WardIndex + 1}, apartment {house.RoomNumber}"
            : $"Ward {house.WardIndex + 1}, plot {house.PlotIndex + 1}" + (house.RoomNumber > 0 ? $", room {house.RoomNumber}" : "");
        return new VenueLocation(house.Id.ToString("x"), label);
    }
}
