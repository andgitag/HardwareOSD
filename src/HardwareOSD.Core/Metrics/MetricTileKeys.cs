namespace HardwareOSD.Core.Metrics;

/// <summary>
/// The single mapping from a Metrics catalogue entry to the key stored on a tile node. Frame
/// metrics need the FPS semantic keys; every other metric uses its raw sensor id when one exists,
/// otherwise the explicit <c>metric:</c> form resolved by the tile sensor map. Keeping this in one
/// place prevents the migration, the source list and the Metrics bridge from drifting.
/// </summary>
public static class MetricTileKeys
{
    public static string For(MetricDefinition definition)
        => definition.Id switch
        {
            "frame.fps" => "fpsCurrent",
            "frame.time" => "frametimeCurrent",
            "frame.fps.1low" => "fps1pctLow",
            _ => MetricCatalog.SensorIdFor(definition) ?? "metric:" + definition.Id,
        };

    public static string For(string metricId)
        => MetricCatalog.Find(metricId) is { } definition ? For(definition) : metricId;
}
