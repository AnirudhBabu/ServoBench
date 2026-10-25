using ServoBench.Core.Drive;
using ServoBench.Core.Modbus;

namespace ServoBench.Presentation.Tests;

/// <summary>In-memory drive with switches for the failure cases the UI must handle.</summary>
public sealed class FakeDriveClient : IDriveClient
{
    public Dictionary<string, double> Values { get; } =
        ParameterCatalog.All.ToDictionary(p => p.Key, p => p.DefaultValue);

    public List<(ParameterDefinition Parameter, double Value)> Writes { get; } = [];

    public HashSet<string> RejectedKeys { get; } = [];

    public DriveStatus Status { get; set; }

    public DriveFault Fault { get; set; }

    public double ActualSpeed { get; set; }

    public double MotorCurrent { get; set; }

    public Exception? ConnectFailure { get; set; }

    public Exception? NextReadFailure { get; set; }

    public bool IsConnected { get; private set; }

    public bool IsDisposed { get; private set; }

    public int SnapshotReads { get; private set; }

    public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<DriveSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (NextReadFailure is { } failure)
        {
            NextReadFailure = null;
            throw failure;
        }

        SnapshotReads++;
        Now = Now.AddMilliseconds(100);
        return Task.FromResult(new DriveSnapshot
        {
            Timestamp = Now,
            Status = Status,
            Fault = Fault,
            ActualSpeed = ActualSpeed,
            MotorCurrent = MotorCurrent,
            DcBusVoltage = 565,
            Temperature = 30,
            Parameters = new Dictionary<string, double>(Values),
        });
    }

    public Task WriteParameterAsync(ParameterDefinition parameter, double value, CancellationToken cancellationToken = default)
    {
        if (RejectedKeys.Contains(parameter.Key))
        {
            throw new ModbusException((byte)ModbusFunctionCode.WriteSingleRegister, ModbusExceptionCode.IllegalDataValue);
        }

        Writes.Add((parameter, value));
        Values[parameter.Key] = value;
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Status = enabled ? Status | DriveStatus.Enabled : Status & ~DriveStatus.Enabled;
        return Task.CompletedTask;
    }

    public Task ResetFaultAsync(CancellationToken cancellationToken = default)
    {
        Fault = DriveFault.None;
        Status &= ~(DriveStatus.Faulted | DriveStatus.Enabled);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeDriveClientFactory : IDriveClientFactory
{
    public FakeDriveClient Client { get; set; } = new();

    public (string Host, int Port)? LastEndpoint { get; private set; }

    public IDriveClient Create(string host, int port)
    {
        LastEndpoint = (host, port);
        return Client;
    }
}

public sealed class FakeProfileFileService : IProfileFileService
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public string? SavePath { get; set; } = "C:/profiles/conveyor.json";

    public string? OpenPath { get; set; } = "C:/profiles/conveyor.json";

    public string? ChooseSavePath(string suggestedFileName) => SavePath;

    public string? ChooseOpenPath() => OpenPath;

    public Task WriteAllTextAsync(string path, string contents)
    {
        Files[path] = contents;
        return Task.CompletedTask;
    }

    public Task<string> ReadAllTextAsync(string path) =>
        Files.TryGetValue(path, out var contents)
            ? Task.FromResult(contents)
            : Task.FromException<string>(new FileNotFoundException("No such file", path));
}
