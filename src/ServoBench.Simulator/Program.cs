using System.Globalization;
using System.Net;
using ServoBench.Simulator;

const int defaultPort = 1502;

var port = defaultPort;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--port" or "-p" && i + 1 < args.Length
        && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    {
        port = parsed;
        i++;
    }
    else if (args[i] is "--help" or "-h")
    {
        Console.WriteLine("servobench-sim [--port 1502]");
        Console.WriteLine("Simulated servo drive exposed over Modbus TCP. Ctrl+C to stop.");
        return;
    }
}

var drive = new SimulatedServoDrive();
await using var clock = new SimulationClock(drive);
await using var server = new ModbusTcpServer(drive, IPAddress.Loopback, port);
// Start log lines with \r and pad them so they overwrite the live status line instead of trailing it.
server.Log += message => Console.WriteLine($"\r[{DateTime.Now:HH:mm:ss}] {message}".PadRight(100));

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

clock.Start();
server.Start();
Console.WriteLine($"ServoBench simulated drive listening on {server.LocalEndpoint} (Modbus TCP, unit 1). Ctrl+C to stop.");

try
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(stop.Token))
    {
        Console.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"\r{(drive.IsEnabled ? "ENABLED " : "disabled")}  speed {drive.ActualSpeed,7:F0} rpm  current {drive.MotorCurrent,6:F2} A  temp {drive.Temperature,5:F1} C  fault {drive.Fault,-16}"));
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Stopping.");
}
