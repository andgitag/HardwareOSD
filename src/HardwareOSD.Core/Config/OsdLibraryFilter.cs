using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Core.Config;

/// <summary>
/// Shared rule for the OSD profile pickers. Retired starter designs disappear from the library,
/// but an active profile or a profile the user edited stays reachable so saved work is never lost.
/// </summary>
public static class OsdLibraryFilter
{
    public static bool IsHiddenFromLibrary(OsdProfile profile, string? activeProfileId)
        => profile.Library == OsdDesignLibrary.CapFrameX &&
           TileDesignCatalog.IsRetired(profile.DesignId) &&
           !profile.HasCustomDesign &&
           !string.Equals(profile.Id, activeProfileId, StringComparison.Ordinal);
}
