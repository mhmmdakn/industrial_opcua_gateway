using System.Buffers.Binary;
using System.Net.Sockets;

namespace DriverGateway.LoadTests.ModbusTcp;

public sealed class RawModbusClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly int _connectTimeoutMs;
    private ushort _transactionId;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public RawModbusClient(string host, int port, byte unitId, int connectTimeoutMs)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort quantity, CancellationToken cancellationToken)
    {
        return await ReadRegistersAsync(0x03, startAddress, quantity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort quantity, CancellationToken cancellationToken)
    {
        return await ReadRegistersAsync(0x04, startAddress, quantity, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken)
    {
        var pdu = new byte[5];
        pdu[0] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), value);

        var response = await SendAndReceiveAsync(0x06, pdu, cancellationToken).ConfigureAwait(false);
        if (response.Length != 5)
        {
            throw new InvalidOperationException("Invalid FC06 response length.");
        }
    }

    public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken)
    {
        if (values.Length is <= 0 or > 123)
        {
            throw new InvalidOperationException($"Write multiple register count must be 1..123, got {values.Length}.");
        }

        var pdu = new byte[6 + (values.Length * 2)];
        pdu[0] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);

        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + (index * 2), 2), values[index]);
        }

        var response = await SendAndReceiveAsync(0x10, pdu, cancellationToken).ConfigureAwait(false);
        if (response.Length != 5)
        {
            throw new InvalidOperationException("Invalid FC16 response length.");
        }

        var echoedStart = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1, 2));
        var echoedCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3, 2));
        if (echoedStart != startAddress || echoedCount != values.Length)
        {
            throw new InvalidOperationException("FC16 response echo mismatch.");
        }
    }

    private async Task<ushort[]> ReadRegistersAsync(byte functionCode, ushort startAddress, ushort quantity, CancellationToken cancellationToken)
    {
        if (quantity is 0 or > 125)
        {
            throw new InvalidOperationException($"Read quantity must be 1..125, got {quantity}.");
        }

        var pdu = new byte[5];
        pdu[0] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), quantity);

        var response = await SendAndReceiveAsync(functionCode, pdu, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2)
        {
            throw new InvalidOperationException("Modbus read response is too short.");
        }

        var byteCount = response[1];
        if (response.Length != byteCount + 2 || byteCount != quantity * 2)
        {
            throw new InvalidOperationException("Modbus read response byte count mismatch.");
        }

        var registers = new ushort[quantity];
        for (var index = 0; index < quantity; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + (index * 2), 2));
        }

        return registers;
    }

    private async Task<byte[]> SendAndReceiveAsync(byte expectedFunctionCode, byte[] pdu, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var stream = _stream ?? throw new InvalidOperationException("Modbus stream unavailable.");

        var transactionId = NextTransactionId();
        var frame = BuildFrame(transactionId, _unitId, pdu);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        var header = new byte[7];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

        var responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        var responseUnitId = header[6];

        if (protocolId != 0)
        {
            throw new InvalidOperationException($"Unexpected protocol id {protocolId}.");
        }

        if (length < 2)
        {
            throw new InvalidOperationException($"Invalid MBAP length {length}.");
        }

        if (responseTransactionId != transactionId)
        {
            throw new InvalidOperationException(
                $"Transaction mismatch. Expected {transactionId}, got {responseTransactionId}.");
        }

        if (responseUnitId != _unitId)
        {
            throw new InvalidOperationException($"UnitId mismatch. Expected {_unitId}, got {responseUnitId}.");
        }

        var responsePdu = new byte[length - 1];
        await ReadExactAsync(stream, responsePdu, cancellationToken).ConfigureAwait(false);
        if (responsePdu.Length == 0)
        {
            throw new InvalidOperationException("Empty Modbus response PDU.");
        }

        var responseFunctionCode = responsePdu[0];
        if (responseFunctionCode == (byte)(expectedFunctionCode | 0x80))
        {
            var exceptionCode = responsePdu.Length > 1 ? responsePdu[1] : (byte)0xFF;
            throw new ModbusDeviceException(expectedFunctionCode, exceptionCode);
        }

        if (responseFunctionCode != expectedFunctionCode)
        {
            throw new InvalidOperationException(
                $"Function mismatch. Expected 0x{expectedFunctionCode:X2}, got 0x{responseFunctionCode:X2}.");
        }

        return responsePdu;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_tcpClient is not null && _tcpClient.Connected && _stream is not null)
        {
            return;
        }

        await ReconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);

        var client = new TcpClient
        {
            NoDelay = true
        };

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_connectTimeoutMs);
        await client.ConnectAsync(_host, _port, connectCts.Token).ConfigureAwait(false);

        _tcpClient = client;
        _stream = client.GetStream();
    }

    public async Task DisposeConnectionAsync()
    {
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }

        try
        {
            _tcpClient?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors.
        }

        _stream = null;
        _tcpClient = null;
    }

    private ushort NextTransactionId()
    {
        unchecked
        {
            _transactionId++;
        }

        return _transactionId;
    }

    private static byte[] BuildFrame(ushort transactionId, byte unitId, byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Remote endpoint closed the connection.");
            }

            totalRead += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
    }
}

public sealed class ModbusDeviceException : Exception
{
    public ModbusDeviceException(byte functionCode, byte exceptionCode)
        : base($"Modbus exception for FC 0x{functionCode:X2}: 0x{exceptionCode:X2}")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }
}
