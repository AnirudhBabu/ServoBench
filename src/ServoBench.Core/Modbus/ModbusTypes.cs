namespace ServoBench.Core.Modbus;

/// <summary>The subset of Modbus function codes a drive configuration tool needs.</summary>
public enum ModbusFunctionCode : byte
{
    ReadHoldingRegisters = 0x03,
    WriteSingleRegister = 0x06,
    WriteMultipleRegisters = 0x10,
}

/// <summary>Standard Modbus exception codes returned by a server in an exception response.</summary>
public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
}

/// <summary>
/// Raised when the device answered with a Modbus exception response.
/// The connection is still healthy; the device rejected the request.
/// </summary>
public sealed class ModbusException : Exception
{
    public ModbusException(byte functionCode, ModbusExceptionCode exceptionCode)
        : base($"Device rejected function 0x{functionCode:X2}: {Describe(exceptionCode)}")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }

    public ModbusExceptionCode ExceptionCode { get; }

    private static string Describe(ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => "illegal function",
        ModbusExceptionCode.IllegalDataAddress => "illegal data address (register missing or read-only)",
        ModbusExceptionCode.IllegalDataValue => "illegal data value (outside the allowed range)",
        ModbusExceptionCode.ServerDeviceFailure => "server device failure",
        _ => $"exception code 0x{(byte)code:X2}",
    };
}

/// <summary>
/// Raised when bytes on the wire do not form a valid Modbus TCP frame.
/// The connection should be treated as broken.
/// </summary>
public sealed class ModbusProtocolException : IOException
{
    public ModbusProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>The 7-byte Modbus Application Protocol header that prefixes every Modbus TCP frame.</summary>
/// <param name="TransactionId">Echoed by the server so responses can be matched to requests.</param>
/// <param name="ProtocolId">Always 0 for Modbus.</param>
/// <param name="Length">Number of bytes that follow: the unit id plus the PDU.</param>
/// <param name="UnitId">Addressed device behind a gateway; 1 by convention for a direct connection.</param>
public readonly record struct MbapHeader(ushort TransactionId, ushort ProtocolId, ushort Length, byte UnitId)
{
    public const int Size = 7;

    /// <summary>Length of the PDU (function code plus data) that follows this header.</summary>
    public int PduLength => Length - 1;
}
