using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Recording;

public enum RecordingState
{
    Idle = 0,
    Recording = 1,
    Stopping = 2,
    Error = 3,
}

public enum RecordingError
{
    None = 0,
    AlreadyRecording = 1,
    NoSensors = 2,
    InvalidDirectory = 3,
    AccessDenied = 4,
    DiskFull = 5,
    IoError = 6,
    Unknown = 7,
}

public sealed record RecordingStatus(
    RecordingState State,
    string? FilePath,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    long Samples,
    long Rows,
    long DroppedSamples,
    long Bytes,
    RecordingError ErrorCode,
    string? ErrorDetail,
    string? AdditionalFilePath = null)
{
    public static readonly RecordingStatus Idle = new(
        RecordingState.Idle, null, default, TimeSpan.Zero, 0, 0, 0, 0, RecordingError.None, null);
}

public readonly record struct RecordingStartResult(
    bool Success,
    RecordingError Error,
    string? Detail,
    string? FilePath,
    RecordingStatus Status)
{
    public static RecordingStartResult Fail(RecordingError error, string detail, RecordingStatus status)
        => new(false, error, detail, null, status);
}

/// <summary>
/// Records the latest immutable <see cref="SensorSnapshot"/> at a fixed interval. Hardware access
/// stays exclusively on the polling thread: this class never calls a provider. Samples are handed
/// to one background writer through a small bounded channel, so a slow disk can never grow memory
/// without bound and the UI thread never performs file I/O.
/// </summary>
public sealed class SensorRecorder : IDisposable
{
    private const int ChannelCapacity = 240;
    private const int FlushIntervalMs = 1000;

    private readonly SensorCache _cache;
    private readonly ILog _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _writerTask;
    private Channel<RecordingSample>? _channel;
    private RecordingSettings _settings = new();
    private IReadOnlyList<RecordingSensor> _sensors = Array.Empty<RecordingSensor>();
    private DateTimeOffset _started;
    private string? _filePath;
    private string? _jsonPath;
    private FileStream? _jsonStream;
    private long _samples;
    private long _rows;
    private long _bytes;
    private long _dropped;
    private RecordingError _errorCode;
    private string? _errorDetail;
    private RecordingState _state = RecordingState.Idle;
    private bool _disposed;

    public SensorRecorder(SensorCache cache, ILog log)
    {
        _cache = cache;
        _log = log;
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _state is RecordingState.Recording or RecordingState.Stopping;
            }
        }
    }

    public RecordingStatus Status
    {
        get
        {
            lock (_gate)
            {
                return BuildStatusLocked();
            }
        }
    }

    public RecordingStartResult Start(RecordingSettings settings, IReadOnlyList<RecordingSensor> sensors)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return RecordingStartResult.Fail(RecordingError.Unknown, "recorder disposed", BuildStatusLocked());
            }

            if (_state is RecordingState.Recording or RecordingState.Stopping)
            {
                return RecordingStartResult.Fail(RecordingError.AlreadyRecording, "recording already active", BuildStatusLocked());
            }

            if (sensors.Count == 0)
            {
                return RecordingStartResult.Fail(RecordingError.NoSensors, "no sensors selected", BuildStatusLocked());
            }

            var normalized = settings.Clone();
            normalized.Normalize();

            var distinctSensors = sensors
                .Where(sensor => !string.IsNullOrWhiteSpace(sensor.Id))
                .GroupBy(sensor => sensor.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (distinctSensors.Length == 0)
            {
                return RecordingStartResult.Fail(RecordingError.NoSensors, "no sensors selected", BuildStatusLocked());
            }

            FileStream stream;
            string path;
            try
            {
                Directory.CreateDirectory(normalized.OutputDirectory);
                path = CreateNewFile(normalized, out stream);
                _jsonStream = null;
                _jsonPath = null;
                if (normalized.Format == RecordingFormat.CsvJson)
                {
                    _jsonPath = Path.ChangeExtension(path, ".json");
                    if (File.Exists(_jsonPath))
                    {
                        _jsonPath = Path.ChangeExtension(path, null) + "-json.json";
                    }

                    _jsonStream = new FileStream(_jsonPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.Read, 65_536, FileOptions.SequentialScan);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                return RecordingStartResult.Fail(RecordingError.AccessDenied, ex.Message, BuildStatusLocked());
            }
            catch (DirectoryNotFoundException ex)
            {
                return RecordingStartResult.Fail(RecordingError.InvalidDirectory, ex.Message, BuildStatusLocked());
            }
            catch (ArgumentException ex)
            {
                return RecordingStartResult.Fail(RecordingError.InvalidDirectory, ex.Message, BuildStatusLocked());
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                return RecordingStartResult.Fail(RecordingError.DiskFull, ex.Message, BuildStatusLocked());
            }
            catch (IOException ex)
            {
                return RecordingStartResult.Fail(RecordingError.IoError, ex.Message, BuildStatusLocked());
            }
            catch (Exception ex)
            {
                return RecordingStartResult.Fail(RecordingError.Unknown, ex.Message, BuildStatusLocked());
            }

            _settings = normalized;
            _sensors = distinctSensors;
            _started = DateTimeOffset.Now;
            _filePath = path;
            _samples = 0;
            _rows = 0;
            _bytes = 0;
            _dropped = 0;
            _errorCode = RecordingError.None;
            _errorDetail = null;
            _state = RecordingState.Recording;

            var channel = Channel.CreateBounded<RecordingSample>(new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _channel = channel;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _captureTask = Task.Factory.StartNew(
                () => CaptureLoopAsync(channel.Writer, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            _writerTask = Task.Factory.StartNew(
                () => WriterLoopAsync(stream, channel.Reader),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            _log.Info("Recording",
                $"Started {normalized.Format} recording at {normalized.IntervalMs} ms to {path} ({_sensors.Count} sensors)");
            return new RecordingStartResult(true, RecordingError.None, null, path, BuildStatusLocked());
        }
    }

    public async Task<RecordingStatus> StopAsync(TimeSpan? timeout = null)
    {
        CancellationTokenSource? cts;
        Channel<RecordingSample>? channel;
        Task? capture;
        Task? writer;
        lock (_gate)
        {
            if (_state == RecordingState.Idle)
            {
                return BuildStatusLocked();
            }

            if (_state == RecordingState.Stopping)
            {
                cts = null;
                channel = _channel;
                capture = _captureTask;
                writer = _writerTask;
            }
            else
            {
                _state = RecordingState.Stopping;
                cts = _cts;
                channel = _channel;
                capture = _captureTask;
                writer = _writerTask;
            }
        }

        cts?.Cancel();
        channel?.Writer.TryComplete();

        var wait = Task.WhenAll(new[] { capture, writer }.Where(t => t is not null).Cast<Task>());
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var completed = await Task.WhenAny(wait, Task.Delay(limit)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, wait))
        {
            _log.Warn("Recording", "Recording stop timed out; the writer will finish on its own");
            return Status;
        }

        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal stop path.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _errorCode = RecordingError.IoError;
                _errorDetail = ex.Message;
                _state = RecordingState.Error;
            }

            _log.Warn("Recording", "Recording stop failed", ex);
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _channel = null;
            _captureTask = null;
            _writerTask = null;
            if (_state == RecordingState.Stopping)
            {
                _state = _errorCode == RecordingError.None ? RecordingState.Idle : RecordingState.Error;
            }

            _log.Info("Recording",
                $"Stopped: {_samples} samples, {_rows} sensor rows, {_bytes} bytes, {_dropped} dropped");
            return BuildStatusLocked();
        }
    }

    private async Task CaptureLoopAsync(ChannelWriter<RecordingSample> writer, CancellationToken token)
    {
        try
        {
            // Sampling only copies the latest immutable snapshot; keep it below the UI and the
            // hardware poller so a busy game cannot be delayed by logging.
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var stopwatch = Stopwatch.StartNew();
            SampleOnce(writer, stopwatch.Elapsed.TotalSeconds);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.IntervalMs));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                SampleOnce(writer, stopwatch.Elapsed.TotalSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _errorCode = RecordingError.Unknown;
                _errorDetail = ex.Message;
                _state = RecordingState.Error;
            }

            _log.Warn("Recording", "Capture loop failed", ex);
            TryCancel();
            writer.TryComplete(ex);
        }
    }

    private void SampleOnce(ChannelWriter<RecordingSample> writer, double elapsedSeconds)
    {
        var snapshot = _cache.Latest;
        var values = new RecordingValue[_sensors.Count];
        for (var i = 0; i < _sensors.Count; i++)
        {
            var descriptor = _sensors[i];
            if (snapshot.TryGet(descriptor.Id, out var value))
            {
                values[i] = new RecordingValue(
                    descriptor.Id,
                    descriptor.DisplayName,
                    value.Available,
                    value.Value,
                    string.IsNullOrWhiteSpace(value.Unit) ? descriptor.Unit : value.Unit,
                    value.Note,
                    value.Source,
                    value.Origin,
                    value.TimestampMs);
            }
            else
            {
                values[i] = new RecordingValue(
                    descriptor.Id,
                    descriptor.DisplayName,
                    false,
                    double.NaN,
                    descriptor.Unit,
                    "sensor-not-present",
                    string.Empty,
                    SensorOrigin.HardwareSensor,
                    0);
            }
        }

        var sample = new RecordingSample(DateTimeOffset.Now, elapsedSeconds, values);
        if (!writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriterLoopAsync(FileStream stream, ChannelReader<RecordingSample> reader)
    {
        var writeText = _settings.Format is RecordingFormat.Csv or RecordingFormat.Txt or RecordingFormat.CsvJson;
        var writeJson = _settings.Format is RecordingFormat.Json or RecordingFormat.CsvJson;
        var jsonStream = writeJson && _settings.Format == RecordingFormat.Json ? stream : _jsonStream;
        StreamWriter? writer = null;
        Utf8JsonWriter? jsonWriter = null;
        try
        {
            if (writeText)
            {
                var encoding = _settings.Format is RecordingFormat.Csv or RecordingFormat.CsvJson
                    ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                    : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                writer = new StreamWriter(stream, encoding, 65_536, leaveOpen: false)
                {
                    AutoFlush = false,
                    NewLine = "\r\n",
                };

                if (_settings.Format is RecordingFormat.Csv or RecordingFormat.CsvJson)
                {
                    await writer.WriteAsync(RecordingTextFormatter.CsvHeader()).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync(RecordingTextFormatter.TxtHeader(_settings, _sensors, _started))
                        .ConfigureAwait(false);
                }
            }

            if (writeJson)
            {
                if (jsonStream is null)
                {
                    throw new IOException("JSON output stream is unavailable");
                }

                jsonWriter = new Utf8JsonWriter(jsonStream, new JsonWriterOptions { Indented = false });
                jsonWriter.WriteStartObject();
                jsonWriter.WriteNumber("schemaVersion", 1);
                jsonWriter.WriteString("startedAt", _started);
                jsonWriter.WriteNumber("intervalMs", _settings.IntervalMs);
                jsonWriter.WriteString("format", _settings.Format.ToString());
                jsonWriter.WriteStartArray("sensors");
                foreach (var sensor in _sensors)
                {
                    jsonWriter.WriteStartObject();
                    jsonWriter.WriteString("sensorId", sensor.Id);
                    jsonWriter.WriteString("displayName", sensor.DisplayName);
                    jsonWriter.WriteString("unit", sensor.Unit);
                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteEndArray();
                jsonWriter.WriteStartArray("samples");
            }

            var flushWatch = Stopwatch.StartNew();
            await foreach (var sample in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (writeText)
                {
                    if (_settings.Format is RecordingFormat.Csv or RecordingFormat.CsvJson)
                    {
                        var builder = new StringBuilder(sample.Values.Count * 160);
                        foreach (var value in sample.Values)
                        {
                            builder.Append(RecordingTextFormatter.CsvRow(sample, value));
                        }

                        await writer!.WriteAsync(builder.ToString()).ConfigureAwait(false);
                        Interlocked.Increment(ref _rows);
                    }
                    else
                    {
                        await writer!.WriteAsync(RecordingTextFormatter.TxtSample(sample)).ConfigureAwait(false);
                        Interlocked.Add(ref _rows, sample.Values.Count);
                    }
                }

                if (writeJson)
                {
                    WriteJsonSample(jsonWriter!, sample);
                    if (!writeText)
                    {
                        Interlocked.Add(ref _rows, sample.Values.Count);
                    }
                }

                Interlocked.Increment(ref _samples);

                if (flushWatch.ElapsedMilliseconds >= FlushIntervalMs)
                {
                    if (writer is not null)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                    }

                    jsonWriter?.Flush();
                    UpdateBytes(stream);
                    flushWatch.Restart();
                }
            }

            if (_settings.Format == RecordingFormat.Txt)
            {
                await writer!.WriteAsync(RecordingTextFormatter.TxtFooter(
                    DateTimeOffset.Now, Interlocked.Read(ref _samples), Interlocked.Read(ref _rows),
                    Interlocked.Read(ref _bytes))).ConfigureAwait(false);
            }

            if (writer is not null)
            {
                await writer.FlushAsync().ConfigureAwait(false);
            }

            if (jsonWriter is not null)
            {
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.Flush();
            }

            UpdateBytes(stream);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _errorCode = ex is IOException io && IsDiskFull(io)
                    ? RecordingError.DiskFull
                    : ex is UnauthorizedAccessException
                        ? RecordingError.AccessDenied
                        : RecordingError.IoError;
                _errorDetail = ex.Message;
                _state = RecordingState.Error;
            }

            _log.Warn("Recording", "Recording writer failed; capture stopped", ex);
            TryCancel();
            reader.TryRead(out _);
        }
        finally
        {
            jsonWriter?.Dispose();
            writer?.Dispose();
            if (!writeText && jsonStream is not null)
            {
                jsonStream.Dispose();
            }
            else if (jsonStream is not null && !ReferenceEquals(jsonStream, stream))
            {
                jsonStream.Dispose();
            }
        }
    }

    private static void WriteJsonSample(Utf8JsonWriter writer, RecordingSample sample)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", sample.Timestamp);
        writer.WriteNumber("elapsedSeconds", Math.Round(sample.ElapsedSeconds, 6));
        writer.WriteStartArray("values");
        foreach (var value in sample.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("sensorId", value.SensorId);
            writer.WriteString("displayName", value.DisplayName);
            if (value.Available && double.IsFinite(value.Value))
            {
                writer.WriteNumber("value", value.Value);
                writer.WriteString("status", "available");
            }
            else
            {
                writer.WriteNull("value");
                writer.WriteString("status", "unavailable");
            }

            writer.WriteString("unit", value.Unit);
            writer.WriteString("source", value.Source ?? string.Empty);
            writer.WriteString("origin", value.Origin.ToString());
            if (value.SensorTimestampMs > 0)
            {
                writer.WriteString("sensorTimestamp",
                    DateTimeOffset.FromUnixTimeMilliseconds(value.SensorTimestampMs));
                var age = sample.Timestamp.ToUnixTimeMilliseconds() - value.SensorTimestampMs;
                if (age >= 0)
                {
                    writer.WriteNumber("ageMs", age);
                }
            }
            else
            {
                writer.WriteNull("sensorTimestamp");
            }

            if (!string.IsNullOrWhiteSpace(value.Note))
            {
                writer.WriteString("note", value.Note);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void UpdateBytes(FileStream stream)
    {
        try
        {
            Interlocked.Exchange(ref _bytes, stream.Length);
        }
        catch
        {
            // A closed/disposed stream during shutdown must not crash the writer.
        }
    }

    private void TryCancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }
    }

    private RecordingStatus BuildStatusLocked()
    {
        var elapsed = _state is RecordingState.Recording or RecordingState.Stopping
            ? DateTimeOffset.Now - _started
            : TimeSpan.Zero;
        var bytes = Interlocked.Read(ref _bytes);
        if (bytes == 0 && _filePath is not null)
        {
            try
            {
                bytes = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
            }
            catch
            {
                // size is diagnostic only
            }
        }

        return new RecordingStatus(
            _state,
            _filePath,
            _started,
            elapsed,
            Interlocked.Read(ref _samples),
            Interlocked.Read(ref _rows),
            Interlocked.Read(ref _dropped),
            bytes,
            _errorCode,
            _errorDetail,
            _jsonPath);
    }

    private static string CreateNewFile(RecordingSettings settings, out FileStream stream)
    {
        var baseName = settings.FileName;
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var name = suffix == 0 ? baseName + settings.Extension : $"{baseName}-{suffix}{settings.Extension}";
            var path = Path.Combine(settings.OutputDirectory, name);
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    65_536, FileOptions.SequentialScan);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Never overwrite an existing recording; try the next suffix.
            }
        }

        throw new IOException("Could not create a unique recording file name in " + settings.OutputDirectory);
    }

    private static bool IsDiskFull(IOException exception)
    {
        const int ErrorDiskFull = 112;
        const int ErrorHandleDiskFull = 39;
        var code = exception.HResult & 0xFFFF;
        return code is ErrorDiskFull or ErrorHandleDiskFull;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
            // shutdown must not throw
        }
    }
}
