using System.Management;

namespace HardwareOSD.Core.Hardware;

/// <summary>
/// One fan reading decoded from the MSI ACPI <c>Get_Fan</c> package.
/// The firmware reports a period counter, not RPM: RPM = 480000 / period.
/// </summary>
public readonly record struct MsiFanReading(int Index, int Period, double Rpm);

public enum MsiFanReadStatus
{
    Ok = 0,
    ClassMissing = 1,
    AccessDenied = 2,
    InstanceUnavailable = 3,
    ReadFailed = 4,
    NotInitialized = 5,
}

/// <summary>Result of one read-only MSI ACPI fan query.</summary>
public sealed record MsiFanReadResult(
    MsiFanReadStatus Status,
    IReadOnlyList<MsiFanReading> Readings,
    string Detail,
    long TimestampMs,
    long LastSuccessfulReadMs)
{
    public bool IsSuccess => Status == MsiFanReadStatus.Ok;
}

/// <summary>
/// Shared, strictly read-only reader for <c>root\wmi:MSI_ACPI.Get_Fan</c>. It is used by the
/// elevated in-process provider and by the optional LocalSystem fan broker so both paths decode the
/// same 32-byte package with the same validation.
///
/// Only <c>Get_Fan</c> is invoked. The class never calls <c>Set_Fan</c>, <c>Set_EC</c>,
/// <c>Set_Data</c>, raw EC ports or any vendor binary. All WMI work is serialized because the
/// documented ACPI control method is not thread-safe.
/// </summary>
public sealed class MsiAcpiFanReader : IDisposable
{
    private const string WmiNamespace = @"root\wmi";
    private const string AcpiClassName = "MSI_ACPI";
    private const string PackageClassName = "Package_32";
    private const int PackageLength = 32;
    private const int RetryInstanceMs = 30_000;
    public const double PeriodToRpm = 480_000.0;

    private readonly object _gate = new();
    private ManagementScope? _scope;
    private ManagementObject? _instance;
    private ManagementClass? _packageClass;
    private bool _disposed;
    private bool _classPresent;
    private bool _accessDenied;
    private long _lastInstanceAttemptMs;
    private long _lastSuccessfulReadMs;
    private string _detail = "not probed";
    private string? _lastError;

    public bool ClassPresent => _classPresent;
    public bool AccessDenied => _accessDenied;
    public bool InstanceOpen => _instance is not null;
    public string Detail => _detail;
    public string? LastError => _lastError;
    public long LastSuccessfulReadMs => Interlocked.Read(ref _lastSuccessfulReadMs);

    /// <summary>Opens the WMI scope and probes the class/instance. Safe to call repeatedly.</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            InitializeLocked();
        }
    }

    /// <summary>
    /// Reads one fan package. The returned result always describes why data is unavailable; the
    /// caller must not synthesize values when <see cref="MsiFanReadResult.IsSuccess"/> is false.
    /// </summary>
    public MsiFanReadResult Read(long timestampMs)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Failure(MsiFanReadStatus.ReadFailed, "reader is disposed", timestampMs);
            }

            if (_scope is null || !_classPresent)
            {
                InitializeLocked();
            }

            if (!_classPresent)
            {
                return Failure(MsiFanReadStatus.ClassMissing, _detail, timestampMs);
            }

            if (_instance is null)
            {
                var now = Environment.TickCount64;
                if (now - _lastInstanceAttemptMs >= RetryInstanceMs)
                {
                    TryOpenInstanceLocked();
                }

                if (_instance is null)
                {
                    return Failure(_accessDenied ? MsiFanReadStatus.AccessDenied : MsiFanReadStatus.InstanceUnavailable,
                        _detail, timestampMs);
                }
            }

            try
            {
                using var package = _packageClass!.CreateInstance();
                package["Bytes"] = new byte[PackageLength];

                using var inParameters = _instance.GetMethodParameters("Get_Fan");
                inParameters["Data"] = package;
                using var outParameters = _instance.InvokeMethod("Get_Fan", inParameters, null);
                if (outParameters?["Data"] is not ManagementBaseObject data ||
                    data["Bytes"] is not byte[] bytes)
                {
                    return Failure(MsiFanReadStatus.ReadFailed, "Get_Fan returned no package", timestampMs);
                }

                var readings = ParseReport(bytes);
                if (readings.Count == 0)
                {
                    return Failure(MsiFanReadStatus.ReadFailed, "Get_Fan returned a failure status", timestampMs);
                }

                _accessDenied = false;
                _lastError = null;
                _detail = "MSI_ACPI Get_Fan is available";
                Interlocked.Exchange(ref _lastSuccessfulReadMs, timestampMs);
                return new MsiFanReadResult(MsiFanReadStatus.Ok, readings, _detail, timestampMs, timestampMs);
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                _accessDenied = true;
                _lastError = "MSI_ACPI requires an elevated process";
                _detail = "MSI_ACPI is installed but requires an elevated process";
                _instance?.Dispose();
                _instance = null;
                return Failure(MsiFanReadStatus.AccessDenied, _detail, timestampMs);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _detail = "Get_Fan failed: " + ex.Message;
                return Failure(MsiFanReadStatus.ReadFailed, _detail, timestampMs);
            }
        }
    }

    private void InitializeLocked()
    {
        if (_disposed || _classPresent)
        {
            return;
        }

        try
        {
            _scope ??= new ManagementScope($@"\\.\{WmiNamespace}");
            _scope.Options.Timeout = TimeSpan.FromSeconds(5);
            _scope.Connect();
            if (!ClassExists(_scope))
            {
                _detail = $"{AcpiClassName} class is not present";
                _lastError = null;
                _classPresent = false;
                return;
            }

            _classPresent = true;
            _packageClass ??= new ManagementClass(_scope, new ManagementPath(PackageClassName), null);
            TryOpenInstanceLocked();
            _lastError = _accessDenied
                ? "MSI_ACPI requires an elevated process"
                : _instance is null ? "MSI_ACPI instance is unavailable" : null;
        }
        catch (Exception ex)
        {
            _classPresent = false;
            _lastError = ex.Message;
            _detail = "MSI ACPI probe failed: " + ex.Message;
        }
    }

    private void TryOpenInstanceLocked()
    {
        _lastInstanceAttemptMs = Environment.TickCount64;
        if (_scope is null)
        {
            return;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(_scope,
                new ObjectQuery($"SELECT * FROM {AcpiClassName}"))
            {
                Options = { Timeout = TimeSpan.FromSeconds(5) },
            };
            using var results = searcher.Get();
            foreach (ManagementObject instance in results)
            {
                _instance?.Dispose();
                _instance = (ManagementObject)instance.Clone();
                _accessDenied = false;
                _detail = "MSI_ACPI Get_Fan is available";
                return;
            }

            _accessDenied = false;
            _detail = "MSI_ACPI has no instance";
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            _accessDenied = true;
            _detail = "MSI_ACPI is installed but requires an elevated process";
        }
        catch (Exception ex)
        {
            _accessDenied = false;
            _detail = "MSI_ACPI instance probe failed: " + ex.Message;
        }
    }

    private static bool ClassExists(ManagementScope scope)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT * FROM meta_class WHERE __CLASS = '{AcpiClassName}'"))
            {
                Options = { Timeout = TimeSpan.FromSeconds(5) },
            };
            using var results = searcher.Get();
            foreach (ManagementObject _ in results)
            {
                return true;
            }
        }
        catch
        {
            // A missing class is reported as absent; the caller stays retryable.
        }

        return false;
    }

    /// <summary>
    /// Decodes a 32-byte Get_Fan response. Returns an empty list when the status byte is zero or
    /// the package is too short. The four readings are big-endian 16-bit period counters starting
    /// at offset 1; a zero period means zero RPM (stopped or unused channel).
    /// </summary>
    public static IReadOnlyList<MsiFanReading> ParseReport(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 9 || bytes[0] == 0)
        {
            return Array.Empty<MsiFanReading>();
        }

        var readings = new List<MsiFanReading>(4);
        for (var index = 0; index < 4; index++)
        {
            var offset = 1 + (index * 2);
            var period = (bytes[offset] << 8) | bytes[offset + 1];
            var rpm = period == 0 ? 0 : PeriodToRpm / period;
            readings.Add(new MsiFanReading(index, period, rpm));
        }

        return readings;
    }

    private MsiFanReadResult Failure(MsiFanReadStatus status, string detail, long timestampMs)
    {
        _lastError = status == MsiFanReadStatus.Ok ? null : detail;
        _detail = detail;
        return new MsiFanReadResult(status, Array.Empty<MsiFanReading>(), detail, timestampMs, LastSuccessfulReadMs);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _packageClass?.Dispose();
            _packageClass = null;
            _instance?.Dispose();
            _instance = null;
            _scope = null;
            _classPresent = false;
        }
    }
}
