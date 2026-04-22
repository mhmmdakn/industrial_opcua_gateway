using System.Buffers.Binary;
using System.Net.Sockets;
using DriverGateway.Core.Models;
using DriverGateway.Plugins.Abstractions;

namespace DriverGateway.Plugins.ModbusTcp;

public sealed class ModbusTcpDriverPlugin : IDriverPlugin
{
    public const string TypeName = "modbus-tcp";
    public string DriverType => TypeName;

    public IChannelRuntime CreateChannelRuntime(ChannelRuntimeContext context)
    {
        return new ModbusTcpChannelRuntime(context);
    }
}

internal enum ModbusWordOrder
{
    HighWordFirst,
    LowWordFirst
}

internal static class ModbusChannelSettingsResolver
{
    public static (string Host, int Port) ParseEndpoint(string rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            throw new InvalidOperationException("Modbus endpoint is required. Expected 'host:port' or 'modbus://host:port'.");
        }

        var normalized = rawEndpoint.Trim();
        if (!normalized.StartsWith("modbus://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"modbus://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("modbus", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException(
                $"Invalid Modbus endpoint '{rawEndpoint}'. Expected 'host:port' or 'modbus://host:port'.");
        }

        if (uri.Port <= 0 || uri.Port > 65535)
        {
            throw new InvalidOperationException($"Invalid Modbus endpoint '{rawEndpoint}'. Port must be between 1 and 65535.");
        }

        return (uri.Host, uri.Port);
    }

    public static byte ParseUnitId(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("unitId", out var rawUnitId) || string.IsNullOrWhiteSpace(rawUnitId))
        {
            return 1;
        }

        if (!int.TryParse(rawUnitId, out var parsed) || parsed is < 1 or > 247)
        {
            throw new InvalidOperationException($"Invalid Modbus unitId '{rawUnitId}'. Valid range is 1..247.");
        }

        return (byte)parsed;
    }

    public static ModbusWordOrder ParseWordOrder(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("wordOrder", out var rawWordOrder) || string.IsNullOrWhiteSpace(rawWordOrder))
        {
            return ModbusWordOrder.HighWordFirst;
        }

        return rawWordOrder.Trim().ToLowerInvariant() switch
        {
            "high-word-first" => ModbusWordOrder.HighWordFirst,
            "low-word-first" => ModbusWordOrder.LowWordFirst,
            _ => throw new InvalidOperationException(
                $"Invalid Modbus wordOrder '{rawWordOrder}'. Valid values are 'high-word-first' or 'low-word-first'.")
        };
    }
}

internal static class ModbusTcpFrameCodec
{
    public static byte[] BuildFrame(ushort transactionId, byte unitId, byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    public static (ushort TransactionId, ushort ProtocolId, ushort Length, byte UnitId) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != 7)
        {
            throw new ModbusProtocolException($"Invalid MBAP header size {header.Length}. Expected 7 bytes.");
        }

        return (
            TransactionId: BinaryPrimitives.ReadUInt16BigEndian(header.Slice(0, 2)),
            ProtocolId: BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2, 2)),
            Length: BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2)),
            UnitId: header[6]);
    }
}

internal sealed class ModbusProtocolException : Exception
{
    public ModbusProtocolException(string message)
        : base(message)
    {
    }
}

internal sealed class ModbusDeviceException : Exception
{
    public ModbusDeviceException(byte functionCode, byte exceptionCode)
        : base($"Modbus exception for FC 0x{functionCode:X2}: 0x{exceptionCode:X2} ({Describe(exceptionCode)}).")
    {
    }

    private static string Describe(byte exceptionCode)
    {
        return exceptionCode switch
        {
            0x01 => "Illegal Function",
            0x02 => "Illegal Data Address",
            0x03 => "Illegal Data Value",
            0x04 => "Slave Device Failure",
            0x05 => "Acknowledge",
            0x06 => "Slave Device Busy",
            0x08 => "Memory Parity Error",
            0x0A => "Gateway Path Unavailable",
            0x0B => "Gateway Target Device Failed to Respond",
            _ => "Unknown"
        };
    }
}

internal sealed class ModbusTcpChannelRuntime : IChannelRuntime
{
    private const byte FunctionReadCoils = 0x01;
    private const byte FunctionReadDiscreteInputs = 0x02;
    private const byte FunctionReadHoldingRegisters = 0x03;
    private const byte FunctionReadInputRegisters = 0x04;
    private const byte FunctionWriteSingleCoil = 0x05;
    private const byte FunctionWriteSingleRegister = 0x06;
    private const byte FunctionWriteMultipleRegisters = 0x10;

    private readonly ChannelRuntimeContext _context;
    private readonly ModbusBatchPlanner _batchPlanner;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private ConnectionState _state = ConnectionState.Disconnected;
    private ushort _transactionId;
    private byte _unitId = 1;
    private ModbusWordOrder _wordOrder = ModbusWordOrder.HighWordFirst;
    private ModbusAddress? _healthCheckAddress;

    public ModbusTcpChannelRuntime(ChannelRuntimeContext context)
    {
        _context = context;

        var maxRegisterSpan = 120;
        if (_context.Channel.Settings.TryGetValue("maxRegisterSpan", out var rawMaxSpan) &&
            int.TryParse(rawMaxSpan, out var parsed))
        {
            maxRegisterSpan = parsed;
        }

        _batchPlanner = new ModbusBatchPlanner(Math.Clamp(maxRegisterSpan, 1, 125));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == ConnectionState.Connected)
            {
                return;
            }

            _state = ConnectionState.Connecting;

            var (host, port) = ModbusChannelSettingsResolver.ParseEndpoint(_context.Channel.Endpoint);
            _unitId = ModbusChannelSettingsResolver.ParseUnitId(_context.Channel.Settings);
            _wordOrder = ModbusChannelSettingsResolver.ParseWordOrder(_context.Channel.Settings);
            _healthCheckAddress = ResolveHealthCheckAddress(_context.TagsByNodeId.Values);

            CloseConnection();

            var tcpClient = new TcpClient { NoDelay = true };
            try
            {
                await tcpClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                tcpClient.Dispose();
                throw;
            }

            _client = tcpClient;
            _stream = tcpClient.GetStream();
            _transactionId = 0;
            _state = ConnectionState.Connected;
            _context.Log?.Invoke(
                $"[MODBUS] Channel '{_context.Channel.Name}' connected to {host}:{port} (unitId={_unitId}, wordOrder={FormatWordOrder(_wordOrder)}).");
        }
        catch
        {
            _state = ConnectionState.Disconnected;
            CloseConnection();
            throw;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state = ConnectionState.Disconnected;
            CloseConnection();
        }
        finally
        {
            _sync.Release();
        }
    }
    public async Task<IReadOnlyCollection<TagReadResult>> ReadAsync(
        IReadOnlyCollection<TagDefinition> demandedTags,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            if (demandedTags.Count == 0)
            {
                await ProbeConnectionAsync(cancellationToken).ConfigureAwait(false);
                return [];
            }

            var now = DateTime.UtcNow;
            var batches = _batchPlanner.BuildBatches(demandedTags);
            var resultsByNodeId = new Dictionary<string, TagReadResult>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in batches)
            {
                var parsedBatchTags = new List<(TagDefinition Tag, ModbusAddress Address)>();
                foreach (var tag in batch.Tags)
                {
                    if (!ModbusAddressParser.TryParse(tag.Address, tag.DataType, out var address) || tag.DataType == TagDataType.String)
                    {
                        resultsByNodeId[tag.NodeIdentifier] = new TagReadResult(tag.NodeIdentifier, null, now, "Bad");
                        continue;
                    }

                    parsedBatchTags.Add((tag, address));
                }

                if (parsedBatchTags.Count == 0)
                {
                    continue;
                }

                foreach (var areaGroup in parsedBatchTags.GroupBy(static item => item.Address.Area))
                {
                    var ordered = areaGroup.OrderBy(static item => item.Address.Offset).ToArray();
                    var start = ordered[0].Address.Offset;
                    var endExclusive = ordered.Max(static item => item.Address.Offset + item.Address.RegisterLength);
                    var quantity = endExclusive - start;

                    ValidateReadRange(areaGroup.Key, start, quantity);

                    if (areaGroup.Key is ModbusArea.Coil or ModbusArea.DiscreteInput)
                    {
                        var bits = await ReadBitsAsync(areaGroup.Key, start, quantity, cancellationToken).ConfigureAwait(false);
                        foreach (var item in ordered)
                        {
                            var relative = item.Address.Offset - start;
                            if (relative < 0 || relative >= bits.Length)
                            {
                                resultsByNodeId[item.Tag.NodeIdentifier] = new TagReadResult(item.Tag.NodeIdentifier, null, now, "Bad");
                                continue;
                            }

                            var bitValue = bits[relative];
                            object typedValue = item.Tag.DataType == TagDataType.Boolean
                                ? bitValue
                                : (bitValue ? 1 : 0);
                            resultsByNodeId[item.Tag.NodeIdentifier] = new TagReadResult(item.Tag.NodeIdentifier, typedValue, now);
                        }

                        continue;
                    }

                    var registers = await ReadRegistersAsync(areaGroup.Key, start, quantity, cancellationToken).ConfigureAwait(false);
                    foreach (var item in ordered)
                    {
                        try
                        {
                            var relative = item.Address.Offset - start;
                            var typedValue = ReadRegisterValue(registers, relative, item.Tag.DataType);
                            resultsByNodeId[item.Tag.NodeIdentifier] = new TagReadResult(item.Tag.NodeIdentifier, typedValue, now);
                        }
                        catch
                        {
                            resultsByNodeId[item.Tag.NodeIdentifier] = new TagReadResult(item.Tag.NodeIdentifier, null, now, "Bad");
                        }
                    }
                }
            }

            var output = new List<TagReadResult>(demandedTags.Count);
            foreach (var tag in demandedTags)
            {
                if (resultsByNodeId.TryGetValue(tag.NodeIdentifier, out var result))
                {
                    output.Add(result);
                    continue;
                }

                output.Add(new TagReadResult(tag.NodeIdentifier, null, now, "Bad"));
            }

            return output;
        }
        catch (Exception ex) when (IsCommunicationException(ex))
        {
            MarkDisconnected("read", ex);
            throw new InvalidOperationException($"Modbus read failed: {ex.Message}", ex);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<WriteResult> WriteAsync(TagDefinition tag, object? value, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            if (!ModbusAddressParser.TryParse(tag.Address, tag.DataType, out var address))
            {
                return WriteResult.Fail($"Unsupported Modbus address: {tag.Address}");
            }

            if (tag.DataType == TagDataType.String)
            {
                return WriteResult.Fail("TagDataType.String is not supported by Modbus TCP runtime.");
            }

            if (address.Area is ModbusArea.InputRegister or ModbusArea.DiscreteInput)
            {
                return WriteResult.Fail("Target address is read-only in Modbus.");
            }

            if (address.Area == ModbusArea.Coil)
            {
                await WriteSingleCoilAsync(address.Offset, ToBoolean(value), cancellationToken).ConfigureAwait(false);
                return WriteResult.Ok();
            }

            await WriteRegisterValueAsync(address, tag.DataType, value, cancellationToken).ConfigureAwait(false);
            return WriteResult.Ok();
        }
        catch (ModbusDeviceException ex)
        {
            return WriteResult.Fail(ex.Message);
        }
        catch (Exception ex) when (IsCommunicationException(ex))
        {
            MarkDisconnected("write", ex);
            return WriteResult.Fail($"Modbus write failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return WriteResult.Fail(ex.Message);
        }
        finally
        {
            _sync.Release();
        }
    }

    public ConnectionState GetConnectionState()
    {
        return _state;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sync.Dispose();
        }
    }

    private async Task ProbeConnectionAsync(CancellationToken cancellationToken)
    {
        if (_healthCheckAddress is null)
        {
            return;
        }

        if (_healthCheckAddress.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
        {
            await ReadBitsAsync(_healthCheckAddress.Area, _healthCheckAddress.Offset, 1, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReadRegistersAsync(_healthCheckAddress.Area, _healthCheckAddress.Offset, 1, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool[]> ReadBitsAsync(ModbusArea area, int startOffset, int quantity, CancellationToken cancellationToken)
    {
        var function = area == ModbusArea.Coil ? FunctionReadCoils : FunctionReadDiscreteInputs;
        var response = await SendRequestAsync(function, BuildReadPdu(function, startOffset, quantity), cancellationToken).ConfigureAwait(false);

        if (response.Length < 2)
        {
            throw new ModbusProtocolException("Modbus bit-read response is too short.");
        }

        var byteCount = response[1];
        if (response.Length != byteCount + 2)
        {
            throw new ModbusProtocolException("Modbus bit-read response byte count mismatch.");
        }

        var bits = new bool[quantity];
        for (var index = 0; index < quantity; index++)
        {
            var sourceByte = response[2 + (index / 8)];
            bits[index] = ((sourceByte >> (index % 8)) & 0x01) == 1;
        }

        return bits;
    }

    private async Task<ushort[]> ReadRegistersAsync(ModbusArea area, int startOffset, int quantity, CancellationToken cancellationToken)
    {
        var function = area == ModbusArea.HoldingRegister ? FunctionReadHoldingRegisters : FunctionReadInputRegisters;
        var response = await SendRequestAsync(function, BuildReadPdu(function, startOffset, quantity), cancellationToken).ConfigureAwait(false);

        if (response.Length < 2)
        {
            throw new ModbusProtocolException("Modbus register-read response is too short.");
        }

        var byteCount = response[1];
        if (response.Length != byteCount + 2)
        {
            throw new ModbusProtocolException("Modbus register-read response byte count mismatch.");
        }

        if (byteCount != quantity * 2)
        {
            throw new ModbusProtocolException("Modbus register-read response payload length mismatch.");
        }

        var output = new ushort[quantity];
        for (var index = 0; index < quantity; index++)
        {
            output[index] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + (index * 2), 2));
        }

        return output;
    }
    private async Task WriteSingleCoilAsync(int offset, bool value, CancellationToken cancellationToken)
    {
        ValidateWriteOffset(offset);

        var pdu = new byte[5];
        pdu[0] = FunctionWriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)offset);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), value ? (ushort)0xFF00 : (ushort)0x0000);

        var response = await SendRequestAsync(FunctionWriteSingleCoil, pdu, cancellationToken).ConfigureAwait(false);
        if (response.Length != 5)
        {
            throw new ModbusProtocolException("Invalid FC05 response length.");
        }
    }

    private async Task WriteRegisterValueAsync(
        ModbusAddress address,
        TagDataType dataType,
        object? value,
        CancellationToken cancellationToken)
    {
        ValidateWriteOffset(address.Offset);

        switch (dataType)
        {
            case TagDataType.Boolean:
            case TagDataType.Int16:
            case TagDataType.UInt16:
            case TagDataType.Word:
            {
                var pdu = new byte[5];
                pdu[0] = FunctionWriteSingleRegister;
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)address.Offset);
                var registerValue = dataType switch
                {
                    TagDataType.Boolean => ToBoolean(value) ? (ushort)1 : (ushort)0,
                    TagDataType.Int16 => unchecked((ushort)Convert.ToInt16(value)),
                    _ => Convert.ToUInt16(value)
                };
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), registerValue);

                var response = await SendRequestAsync(FunctionWriteSingleRegister, pdu, cancellationToken).ConfigureAwait(false);
                if (response.Length != 5)
                {
                    throw new ModbusProtocolException("Invalid FC06 response length.");
                }

                return;
            }
            case TagDataType.Int32:
                await WriteMultipleRegistersAsync(address.Offset, BuildInt32Registers(Convert.ToInt32(value)), cancellationToken).ConfigureAwait(false);
                return;
            case TagDataType.UInt32:
            case TagDataType.DWord:
                await WriteMultipleRegistersAsync(address.Offset, BuildUInt32Registers(Convert.ToUInt32(value)), cancellationToken).ConfigureAwait(false);
                return;
            case TagDataType.Float:
                await WriteMultipleRegistersAsync(address.Offset, BuildFloatRegisters(Convert.ToSingle(value)), cancellationToken).ConfigureAwait(false);
                return;
            case TagDataType.Double:
                await WriteMultipleRegistersAsync(address.Offset, BuildDoubleRegisters(Convert.ToDouble(value)), cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"Unsupported Modbus write data type: {dataType}");
        }
    }

    private async Task WriteMultipleRegistersAsync(int startOffset, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
    {
        if (values.Count is <= 0 or > 123)
        {
            throw new InvalidOperationException($"Invalid register write count: {values.Count}. Valid range is 1..123.");
        }

        ValidateWriteOffset(startOffset);
        ValidateWriteOffset(startOffset + values.Count - 1);

        var pdu = new byte[6 + (values.Count * 2)];
        pdu[0] = FunctionWriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)startOffset);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)values.Count);
        pdu[5] = (byte)(values.Count * 2);

        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + (index * 2), 2), values[index]);
        }

        var response = await SendRequestAsync(FunctionWriteMultipleRegisters, pdu, cancellationToken).ConfigureAwait(false);
        if (response.Length != 5)
        {
            throw new ModbusProtocolException("Invalid FC16 response length.");
        }

        var echoedStart = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1, 2));
        var echoedCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3, 2));
        if (echoedStart != startOffset || echoedCount != values.Count)
        {
            throw new ModbusProtocolException("FC16 response echo mismatch.");
        }
    }

    private async Task<byte[]> SendRequestAsync(byte expectedFunctionCode, byte[] pdu, CancellationToken cancellationToken)
    {
        var stream = GetStream();
        var transactionId = NextTransactionId();
        var frame = ModbusTcpFrameCodec.BuildFrame(transactionId, _unitId, pdu);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        var header = new byte[7];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var parsedHeader = ModbusTcpFrameCodec.ParseHeader(header);

        if (parsedHeader.ProtocolId != 0)
        {
            throw new ModbusProtocolException($"Unexpected protocol id {parsedHeader.ProtocolId} in Modbus response.");
        }

        if (parsedHeader.TransactionId != transactionId)
        {
            throw new ModbusProtocolException($"Transaction mismatch. Expected {transactionId}, got {parsedHeader.TransactionId}.");
        }

        if (parsedHeader.UnitId != _unitId)
        {
            throw new ModbusProtocolException($"UnitId mismatch. Expected {_unitId}, got {parsedHeader.UnitId}.");
        }

        if (parsedHeader.Length < 2)
        {
            throw new ModbusProtocolException($"Invalid MBAP length {parsedHeader.Length}.");
        }

        var responsePdu = new byte[parsedHeader.Length - 1];
        await ReadExactAsync(stream, responsePdu, cancellationToken).ConfigureAwait(false);

        var actualFunction = responsePdu[0];
        if (actualFunction == (expectedFunctionCode | 0x80))
        {
            if (responsePdu.Length < 2)
            {
                throw new ModbusProtocolException("Invalid Modbus exception response length.");
            }

            throw new ModbusDeviceException(expectedFunctionCode, responsePdu[1]);
        }

        if (actualFunction != expectedFunctionCode)
        {
            throw new ModbusProtocolException(
                $"Function code mismatch. Expected 0x{expectedFunctionCode:X2}, got 0x{actualFunction:X2}.");
        }

        return responsePdu;
    }

    private ushort NextTransactionId()
    {
        unchecked
        {
            _transactionId++;
        }

        return _transactionId;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Remote endpoint closed the Modbus TCP connection.");
            }

            total += read;
        }
    }

    private static byte[] BuildReadPdu(byte function, int startOffset, int quantity)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)startOffset);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)quantity);
        return pdu;
    }

    private object ReadRegisterValue(ushort[] registers, int relativeOffset, TagDataType dataType)
    {
        if (relativeOffset < 0 || relativeOffset >= registers.Length)
        {
            throw new InvalidOperationException("Register read offset is out of range.");
        }

        return dataType switch
        {
            TagDataType.Boolean => registers[relativeOffset] != 0,
            TagDataType.Int16 => unchecked((short)registers[relativeOffset]),
            TagDataType.UInt16 => registers[relativeOffset],
            TagDataType.Word => registers[relativeOffset],
            TagDataType.Int32 => ReadInt32(registers, relativeOffset),
            TagDataType.UInt32 => ReadUInt32(registers, relativeOffset),
            TagDataType.DWord => ReadUInt32(registers, relativeOffset),
            TagDataType.Float => ReadFloat(registers, relativeOffset),
            TagDataType.Double => ReadDouble(registers, relativeOffset),
            TagDataType.String => throw new InvalidOperationException("String is not supported in Modbus runtime."),
            _ => registers[relativeOffset]
        };
    }

    private int ReadInt32(ushort[] registers, int offset)
    {
        if (offset + 1 >= registers.Length)
        {
            throw new InvalidOperationException("Insufficient register data for Int32.");
        }

        Span<byte> bytes = stackalloc byte[4];
        var ordered = GetOrderedWordsForRead(registers.AsSpan(offset, 2));
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(index * 2, 2), ordered[index]);
        }

        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private double ReadDouble(ushort[] registers, int offset)
    {
        if (offset + 3 >= registers.Length)
        {
            throw new InvalidOperationException("Insufficient register data for Double.");
        }

        Span<byte> bytes = stackalloc byte[8];
        var ordered = GetOrderedWordsForRead(registers.AsSpan(offset, 4));
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(index * 2, 2), ordered[index]);
        }

        if (BitConverter.IsLittleEndian)
        {
            bytes.Reverse();
        }

        return BitConverter.ToDouble(bytes);
    }

    private uint ReadUInt32(ushort[] registers, int offset)
    {
        return unchecked((uint)ReadInt32(registers, offset));
    }

    private float ReadFloat(ushort[] registers, int offset)
    {
        if (offset + 1 >= registers.Length)
        {
            throw new InvalidOperationException("Insufficient register data for Float.");
        }

        Span<byte> bytes = stackalloc byte[4];
        var ordered = GetOrderedWordsForRead(registers.AsSpan(offset, 2));
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(index * 2, 2), ordered[index]);
        }

        if (BitConverter.IsLittleEndian)
        {
            bytes.Reverse();
        }

        return BitConverter.ToSingle(bytes);
    }
    private ushort[] BuildInt32Registers(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);

        var words = new[]
        {
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2))
        };

        return ApplyWordOrderForWrite(words);
    }

    private ushort[] BuildDoubleRegisters(double value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        var words = new ushort[4];
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index * 2, 2));
        }

        return ApplyWordOrderForWrite(words);
    }

    private ushort[] BuildUInt32Registers(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        var words = new[]
        {
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2))
        };

        return ApplyWordOrderForWrite(words);
    }

    private ushort[] BuildFloatRegisters(float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        var words = new ushort[2];
        words[0] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2));
        words[1] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        return ApplyWordOrderForWrite(words);
    }

    private ushort[] GetOrderedWordsForRead(ReadOnlySpan<ushort> words)
    {
        var output = words.ToArray();
        if (_wordOrder == ModbusWordOrder.LowWordFirst)
        {
            Array.Reverse(output);
        }

        return output;
    }

    private ushort[] ApplyWordOrderForWrite(ushort[] words)
    {
        if (_wordOrder == ModbusWordOrder.LowWordFirst)
        {
            Array.Reverse(words);
        }

        return words;
    }

    private static bool ToBoolean(object? value)
    {
        return value switch
        {
            null => false,
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            sbyte sbyteValue => sbyteValue != 0,
            short shortValue => shortValue != 0,
            ushort ushortValue => ushortValue != 0,
            int intValue => intValue != 0,
            uint uintValue => uintValue != 0,
            long longValue => longValue != 0,
            ulong ulongValue => ulongValue != 0,
            float floatValue => Math.Abs(floatValue) > float.Epsilon,
            double doubleValue => Math.Abs(doubleValue) > double.Epsilon,
            decimal decimalValue => decimalValue != 0,
            string stringValue when bool.TryParse(stringValue, out var parsedBool) => parsedBool,
            string stringValue when int.TryParse(stringValue, out var parsedInt) => parsedInt != 0,
            _ => Convert.ToBoolean(value)
        };
    }

    private NetworkStream GetStream()
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Modbus channel stream is not available.");
        }

        return _stream;
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _stream is null || _client is null || !_client.Connected)
        {
            throw new InvalidOperationException("Modbus channel runtime is disconnected.");
        }
    }

    private void MarkDisconnected(string operation, Exception ex)
    {
        _state = ConnectionState.Disconnected;
        CloseConnection();
        _context.Log?.Invoke($"[MODBUS] Channel '{_context.Channel.Name}' {operation} failure: {ex.Message}");
    }

    private void CloseConnection()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Ignore cleanup failures.
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // Ignore cleanup failures.
        }

        _stream = null;
        _client = null;
    }

    private static bool IsCommunicationException(Exception ex)
    {
        return ex is IOException
            or SocketException
            or ObjectDisposedException
            or ModbusProtocolException;
    }

    private static void ValidateReadRange(ModbusArea area, int startOffset, int quantity)
    {
        if (startOffset < 0 || startOffset > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Invalid Modbus start offset: {startOffset}.");
        }

        if (quantity <= 0)
        {
            throw new InvalidOperationException("Modbus read quantity must be greater than zero.");
        }

        var maxCount = area is ModbusArea.Coil or ModbusArea.DiscreteInput ? 2000 : 125;
        if (quantity > maxCount)
        {
            throw new InvalidOperationException(
                $"Modbus read quantity {quantity} exceeds max {maxCount} for area {area}. Reduce maxRegisterSpan.");
        }

        var endOffset = startOffset + quantity - 1;
        if (endOffset > ushort.MaxValue)
        {
            throw new InvalidOperationException("Modbus read range exceeds address limit 65535.");
        }
    }

    private static void ValidateWriteOffset(int offset)
    {
        if (offset < 0 || offset > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Invalid Modbus write offset: {offset}.");
        }
    }

    private static ModbusAddress? ResolveHealthCheckAddress(IEnumerable<TagDefinition> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.DataType == TagDataType.String)
            {
                continue;
            }

            if (ModbusAddressParser.TryParse(tag.Address, tag.DataType, out var address))
            {
                return address with { RegisterLength = 1 };
            }
        }

        return null;
    }

    private static string FormatWordOrder(ModbusWordOrder wordOrder)
    {
        return wordOrder == ModbusWordOrder.LowWordFirst
            ? "low-word-first"
            : "high-word-first";
    }
}
