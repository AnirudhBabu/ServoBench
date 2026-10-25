using ServoBench.Core.Modbus;
using Xunit;

namespace ServoBench.Core.Tests;

public class ModbusFrameTests
{
    [Fact]
    public void Read_request_matches_the_spec_byte_layout()
    {
        var pdu = ModbusFrame.ReadHoldingRegistersPdu(startAddress: 0x006B, count: 3);
        var adu = ModbusFrame.BuildAdu(transactionId: 0x0001, unitId: 0x11, pdu);

        // Example straight from the Modbus application protocol spec, wrapped in an MBAP header.
        Assert.Equal(
            new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x11, 0x03, 0x00, 0x6B, 0x00, 0x03 },
            adu);
    }

    [Fact]
    public void Write_multiple_request_carries_byte_count_and_big_endian_values()
    {
        var pdu = ModbusFrame.WriteMultipleRegistersPdu(startAddress: 1, [0x000A, 0x0102]);

        Assert.Equal(new byte[] { 0x10, 0x00, 0x01, 0x00, 0x02, 0x04, 0x00, 0x0A, 0x01, 0x02 }, pdu);
    }

    [Fact]
    public void Header_round_trips()
    {
        var adu = ModbusFrame.BuildAdu(0xBEEF, 7, [0x03, 0x00, 0x00, 0x00, 0x01]);

        var header = ModbusFrame.ParseHeader(adu);

        Assert.Equal(new MbapHeader(0xBEEF, 0, 6, 7), header);
        Assert.Equal(5, header.PduLength);
    }

    [Fact]
    public void Header_with_non_zero_protocol_id_is_rejected()
    {
        byte[] header = [0x00, 0x01, 0x00, 0x01, 0x00, 0x06, 0x01];

        Assert.Throws<ModbusProtocolException>(() => ModbusFrame.ParseHeader(header));
    }

    [Fact]
    public void Read_response_is_decoded_to_registers()
    {
        byte[] response = [0x03, 0x06, 0x02, 0x2B, 0x00, 0x00, 0x00, 0x64];

        var values = ModbusFrame.ParseReadHoldingRegistersResponse(response, expectedCount: 3);

        Assert.Equal(new ushort[] { 0x022B, 0x0000, 0x0064 }, values);
    }

    [Fact]
    public void Read_response_with_wrong_byte_count_is_a_protocol_error()
    {
        byte[] response = [0x03, 0x04, 0x00, 0x01, 0x00, 0x02];

        Assert.Throws<ModbusProtocolException>(() => ModbusFrame.ParseReadHoldingRegistersResponse(response, expectedCount: 3));
    }

    [Fact]
    public void Exception_response_surfaces_the_device_exception_code()
    {
        byte[] response = [0x83, 0x02];

        var ex = Assert.Throws<ModbusException>(() => ModbusFrame.ParseReadHoldingRegistersResponse(response, 1));

        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.ExceptionCode);
        Assert.Equal(0x03, ex.FunctionCode);
    }

    [Fact]
    public void Write_response_must_echo_the_request()
    {
        var request = ModbusFrame.WriteSingleRegisterPdu(10, 1500);
        byte[] wrongEcho = [0x06, 0x00, 0x0A, 0x00, 0x00];

        ModbusFrame.ParseWriteResponse(request, request); // a correct echo passes
        Assert.Throws<ModbusProtocolException>(() => ModbusFrame.ParseWriteResponse(wrongEcho, request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void Read_count_outside_spec_limits_is_rejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusFrame.ReadHoldingRegistersPdu(0, (ushort)count));
    }
}
