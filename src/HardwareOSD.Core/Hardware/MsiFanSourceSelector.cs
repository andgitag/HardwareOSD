namespace HardwareOSD.Core.Hardware;

public enum MsiFanSource
{
    None = 0,
    DirectWmi = 1,
    Broker = 2,
}

/// <summary>
/// Chooses exactly one MSI fan source. The direct WMI path is reserved for elevated processes; the
/// broker is used only when the process is not elevated, the user enabled it, and the service is
/// installed. No caller ever publishes both paths at once.
/// </summary>
public static class MsiFanSourceSelector
{
    public static MsiFanSource Select(bool msiEnabled, bool brokerEnabled, bool elevated, bool brokerInstalled)
    {
        if (!msiEnabled)
        {
            return MsiFanSource.None;
        }

        if (elevated)
        {
            return MsiFanSource.DirectWmi;
        }

        return brokerEnabled && brokerInstalled ? MsiFanSource.Broker : MsiFanSource.DirectWmi;
    }
}
