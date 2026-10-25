using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ServoBench.Core.Modbus;

namespace ServoBench.Simulator;

/// <summary>Turns a request PDU into a response PDU against a register bank.</summary>
public static class ModbusRequestProcessor
{
    public static byte[] Process(ReadOnlySpan<byte> requestPdu, IRegisterBank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (requestPdu.IsEmpty)
        {
            throw new ModbusProtocolException("Empty request PDU.");
        }

        var function = requestPdu[0];
        try
        {
            return (ModbusFunctionCode)function switch
            {
                ModbusFunctionCode.ReadHoldingRegisters => ReadHolding(requestPdu, bank),
                ModbusFunctionCode.WriteSingleRegister => WriteSingle(requestPdu, bank),
                ModbusFunctionCode.WriteMultipleRegisters => WriteMultiple(requestPdu, bank),
                _ => ModbusFrame.ExceptionPdu(function, ModbusExceptionCode.IllegalFunction),
            };
        }
        catch (RegisterAccessException ex)
        {
            return ModbusFrame.ExceptionPdu(function, ex.Code);
        }
    }

    private static byte[] ReadHolding(ReadOnlySpan<byte> pdu, IRegisterBank bank)
    {
        if (pdu.Length != 5)
        {
            return ModbusFrame.ExceptionPdu(pdu[0], ModbusExceptionCode.IllegalDataValue);
        }

        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (count is < 1 or > ModbusFrame.MaxReadRegisters)
        {
            return ModbusFrame.ExceptionPdu(pdu[0], ModbusExceptionCode.IllegalDataValue);
        }

        return ModbusFrame.ReadHoldingRegistersResponsePdu(bank.ReadRegisters(start, count));
    }

    private static byte[] WriteSingle(ReadOnlySpan<byte> pdu, IRegisterBank bank)
    {
        if (pdu.Length != 5)
        {
            return ModbusFrame.ExceptionPdu(pdu[0], ModbusExceptionCode.IllegalDataValue);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        bank.WriteRegisters(address, [value]);
        return pdu.ToArray(); // the response echoes the request
    }

    private static byte[] WriteMultiple(ReadOnlySpan<byte> pdu, IRegisterBank bank)
    {
        if (pdu.Length < 6)
        {
            return ModbusFrame.ExceptionPdu(pdu[0], ModbusExceptionCode.IllegalDataValue);
        }

        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        var byteCount = pdu[5];
        if (count is < 1 or > ModbusFrame.MaxWriteRegisters || byteCount != count * 2 || pdu.Length != 6 + byteCount)
        {
            return ModbusFrame.ExceptionPdu(pdu[0], ModbusExceptionCode.IllegalDataValue);
        }

        var values = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + (i * 2))..]);
        }

        bank.WriteRegisters(start, values);
        return pdu[..5].ToArray(); // function, start address, quantity
    }
}

/// <summary>A small multi-client Modbus TCP server in front of a register bank.</summary>
public sealed class ModbusTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IRegisterBank _bank;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _connections = [];
    private Task? _acceptLoop;
    private bool _disposed;

    public ModbusTcpServer(IRegisterBank bank, IPAddress address, int port)
    {
        _bank = bank ?? throw new ArgumentNullException(nameof(bank));
        _listener = new TcpListener(address, port);
    }

    /// <summary>The bound endpoint. Useful when started on port 0 to get an ephemeral port.</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    public event Action<string>? Log;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        Task[] pending;
        lock (_connections)
        {
            pending = [.. _connections];
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var connection = ServeClientAsync(client, cancellationToken);
            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(connection);
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "client";
        Log?.Invoke($"{remote} connected");

        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[MbapHeader.Size];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                    var mbap = ModbusFrame.ParseHeader(header);

                    var request = new byte[mbap.PduLength];
                    await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);

                    var response = ModbusRequestProcessor.Process(request, _bank);
                    var adu = ModbusFrame.BuildAdu(mbap.TransactionId, mbap.UnitId, response);
                    await stream.WriteAsync(adu, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // server shutting down
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // client went away, or sent a malformed frame
            }
        }

        Log?.Invoke($"{remote} disconnected");
    }
}
