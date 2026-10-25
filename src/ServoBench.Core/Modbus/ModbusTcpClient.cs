using System.Net.Sockets;

namespace ServoBench.Core.Modbus;

/// <summary>Holding-register access to a Modbus device. Abstracted so drive logic can be tested without sockets.</summary>
public interface IModbusClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default);

    Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default);

    Task WriteMultipleRegistersAsync(ushort startAddress, IReadOnlyList<ushort> values, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal Modbus TCP client. One request is in flight at a time; transaction ids are checked
/// on every response. Any timeout or framing error closes the socket, because the stream can no
/// longer be trusted to be aligned on a frame boundary.
/// </summary>
public sealed class ModbusTcpClient : IModbusClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public ModbusTcpClient(string host, int port, byte unitId = 1, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        _host = host;
        _port = port;
        _unitId = unitId;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseConnection();

            var tcp = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await tcp.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"Could not reach {_host}:{_port} within {_timeout.TotalSeconds:0.#} s.");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            _tcp = tcp;
            _stream = tcp.GetStream();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
    {
        var request = ModbusFrame.ReadHoldingRegistersPdu(startAddress, count);
        var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        return ModbusFrame.ParseReadHoldingRegistersResponse(response, count);
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        var request = ModbusFrame.WriteSingleRegisterPdu(address, value);
        var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        ModbusFrame.ParseWriteResponse(response, request);
    }

    public async Task WriteMultipleRegistersAsync(ushort startAddress, IReadOnlyList<ushort> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var request = ModbusFrame.WriteMultipleRegistersPdu(startAddress, values.ToArray());
        var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        ModbusFrame.ParseWriteResponse(response, request);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseConnection();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<byte[]> ExchangeAsync(byte[] requestPdu, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
            var transactionId = unchecked(++_transactionId);
            var adu = ModbusFrame.BuildAdu(transactionId, _unitId, requestPdu);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                await stream.WriteAsync(adu, timeoutCts.Token).ConfigureAwait(false);

                var headerBytes = new byte[MbapHeader.Size];
                await stream.ReadExactlyAsync(headerBytes, timeoutCts.Token).ConfigureAwait(false);
                var header = ModbusFrame.ParseHeader(headerBytes);

                var pdu = new byte[header.PduLength];
                await stream.ReadExactlyAsync(pdu, timeoutCts.Token).ConfigureAwait(false);

                if (header.TransactionId != transactionId)
                {
                    throw new ModbusProtocolException(
                        $"Response transaction id {header.TransactionId} does not match request {transactionId}.");
                }

                if (header.UnitId != _unitId)
                {
                    throw new ModbusProtocolException($"Response came from unit {header.UnitId}, expected {_unitId}.");
                }

                return pdu;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                CloseConnection();
                throw new TimeoutException($"No response from {_host}:{_port} within {_timeout.TotalSeconds:0.#} s.");
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
            {
                CloseConnection();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CloseConnection()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }
}
