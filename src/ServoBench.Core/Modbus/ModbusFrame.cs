using System.Buffers.Binary;

namespace ServoBench.Core.Modbus;

/// <summary>
/// Pure encode/decode helpers for Modbus TCP frames. No I/O lives here, which keeps
/// the byte layout fully unit-testable.
/// </summary>
public static class ModbusFrame
{
    /// <summary>Spec limit for registers in a single read (function 0x03).</summary>
    public const int MaxReadRegisters = 125;

    /// <summary>Spec limit for registers in a single write (function 0x10).</summary>
    public const int MaxWriteRegisters = 123;

    /// <summary>Largest PDU allowed by the spec (253 bytes).</summary>
    public const int MaxPduLength = 253;

    private const byte ExceptionFlag = 0x80;

    public static byte[] BuildAdu(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (pdu.IsEmpty || pdu.Length > MaxPduLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pdu), $"PDU must be 1..{MaxPduLength} bytes.");
        }

        var adu = new byte[MbapHeader.Size + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(pdu.Length + 1));
        adu[6] = unitId;
        pdu.CopyTo(adu.AsSpan(MbapHeader.Size));
        return adu;
    }

    public static MbapHeader ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < MbapHeader.Size)
        {
            throw new ModbusProtocolException($"MBAP header needs {MbapHeader.Size} bytes, got {header.Length}.");
        }

        var parsed = new MbapHeader(
            BinaryPrimitives.ReadUInt16BigEndian(header),
            BinaryPrimitives.ReadUInt16BigEndian(header[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(header[4..]),
            header[6]);

        if (parsed.ProtocolId != 0)
        {
            throw new ModbusProtocolException($"Unexpected protocol id {parsed.ProtocolId}; Modbus uses 0.");
        }

        if (parsed.PduLength < 1 || parsed.PduLength > MaxPduLength)
        {
            throw new ModbusProtocolException($"Invalid MBAP length field {parsed.Length}.");
        }

        return parsed;
    }

    // ---------------------------------------------------------------- requests (client side)

    public static byte[] ReadHoldingRegistersPdu(ushort startAddress, ushort count)
    {
        if (count is < 1 or > MaxReadRegisters)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"Count must be 1..{MaxReadRegisters}.");
        }

        EnsureAddressRange(startAddress, count);

        var pdu = new byte[5];
        pdu[0] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        return pdu;
    }

    public static byte[] WriteSingleRegisterPdu(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)ModbusFunctionCode.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] WriteMultipleRegistersPdu(ushort startAddress, ReadOnlySpan<ushort> values)
    {
        if (values.Length is < 1 or > MaxWriteRegisters)
        {
            throw new ArgumentOutOfRangeException(nameof(values), $"Value count must be 1..{MaxWriteRegisters}.");
        }

        EnsureAddressRange(startAddress, values.Length);

        var pdu = new byte[6 + (values.Length * 2)];
        pdu[0] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + (i * 2)), values[i]);
        }

        return pdu;
    }

    // ---------------------------------------------------------------- responses (client side)

    /// <summary>Throws <see cref="ModbusException"/> if the PDU is an exception response, or
    /// <see cref="ModbusProtocolException"/> if it answers a different function.</summary>
    public static void EnsureSuccess(ReadOnlySpan<byte> responsePdu, ModbusFunctionCode expected)
    {
        if (responsePdu.IsEmpty)
        {
            throw new ModbusProtocolException("Empty response PDU.");
        }

        var function = responsePdu[0];
        if (function == ((byte)expected | ExceptionFlag))
        {
            if (responsePdu.Length < 2)
            {
                throw new ModbusProtocolException("Exception response is missing its exception code.");
            }

            throw new ModbusException((byte)expected, (ModbusExceptionCode)responsePdu[1]);
        }

        if (function != (byte)expected)
        {
            throw new ModbusProtocolException(
                $"Expected function 0x{(byte)expected:X2} in response, got 0x{function:X2}.");
        }
    }

    public static ushort[] ParseReadHoldingRegistersResponse(ReadOnlySpan<byte> responsePdu, ushort expectedCount)
    {
        EnsureSuccess(responsePdu, ModbusFunctionCode.ReadHoldingRegisters);

        if (responsePdu.Length < 2)
        {
            throw new ModbusProtocolException("Read response is missing its byte count.");
        }

        var byteCount = responsePdu[1];
        if (byteCount != expectedCount * 2 || responsePdu.Length != 2 + byteCount)
        {
            throw new ModbusProtocolException(
                $"Read response carries {byteCount} bytes; expected {expectedCount * 2}.");
        }

        var values = new ushort[expectedCount];
        for (var i = 0; i < expectedCount; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(responsePdu[(2 + (i * 2))..]);
        }

        return values;
    }

    /// <summary>Write responses echo the address and value/quantity. Checks the echo matches.</summary>
    public static void ParseWriteResponse(ReadOnlySpan<byte> responsePdu, ReadOnlySpan<byte> requestPdu)
    {
        var function = (ModbusFunctionCode)requestPdu[0];
        EnsureSuccess(responsePdu, function);

        if (responsePdu.Length != 5 || !responsePdu[1..5].SequenceEqual(requestPdu[1..5]))
        {
            throw new ModbusProtocolException("Write response does not echo the request.");
        }
    }

    // ---------------------------------------------------------------- server side

    public static byte[] ExceptionPdu(byte functionCode, ModbusExceptionCode code) =>
        [(byte)(functionCode | ExceptionFlag), (byte)code];

    public static byte[] ReadHoldingRegistersResponsePdu(ReadOnlySpan<ushort> values)
    {
        var pdu = new byte[2 + (values.Length * 2)];
        pdu[0] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
        pdu[1] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (i * 2)), values[i]);
        }

        return pdu;
    }

    private static void EnsureAddressRange(ushort startAddress, int count)
    {
        if (startAddress + count - 1 > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(startAddress), "Register range runs past address 65535.");
        }
    }
}
