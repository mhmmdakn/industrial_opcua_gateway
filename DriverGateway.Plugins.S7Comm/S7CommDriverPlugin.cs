using DriverGateway.Core.Models;
using DriverGateway.Plugins.Abstractions;

namespace DriverGateway.Plugins.S7Comm;

public sealed class S7CommDriverPlugin : IDriverPlugin
{
    public const string TypeName = "s7comm";

    public string DriverType => TypeName;

    public IChannelRuntime CreateChannelRuntime(ChannelRuntimeContext context)
    {
        return new S7CommChannelRuntime(context);
    }
}

internal sealed class S7CommChannelRuntime : IChannelRuntime
{
    private readonly ChannelRuntimeContext _context;
    private readonly object _syncRoot = new();
    private readonly S7BatchPlanner _batchPlanner;

    private readonly Dictionary<int, byte[]> _dbMemory = [];
    private ConnectionState _state = ConnectionState.Disconnected;

    public S7CommChannelRuntime(ChannelRuntimeContext context)
    {
        _context = context;

        var maxBlockBytes = 222;
        if (_context.Channel.Settings.TryGetValue("maxBlockBytes", out var rawMaxBlockBytes) &&
            int.TryParse(rawMaxBlockBytes, out var parsed))
        {
            maxBlockBytes = parsed;
        }

        _batchPlanner = new S7BatchPlanner(maxBlockBytes);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            _state = ConnectionState.Connecting;
            EnsureSeedMemory();
            _state = ConnectionState.Connected;
        }

        _context.Log?.Invoke($"[S7] Channel '{_context.Channel.Name}' connected (in-house runtime).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            _state = ConnectionState.Disconnected;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<TagReadResult>> ReadAsync(
        IReadOnlyCollection<TagDefinition> demandedTags,
        CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            EnsureConnected();

            if (demandedTags.Count == 0)
            {
                return Task.FromResult<IReadOnlyCollection<TagReadResult>>([]);
            }

            var now = DateTime.UtcNow;
            var results = new List<TagReadResult>(demandedTags.Count);

            // Batch plan is calculated to enforce contiguous DB block reads.
            var batches = _batchPlanner.BuildBatches(demandedTags);
            foreach (var batch in batches)
            {
                foreach (var tag in batch.Tags)
                {
                    if (!S7AddressParser.TryParse(tag.Address, out var address))
                    {
                        continue;
                    }

                    var value = ReadTypedValue(address, tag.DataType);
                    results.Add(new TagReadResult(tag.NodeIdentifier, value, now));
                }
            }

            return Task.FromResult<IReadOnlyCollection<TagReadResult>>(results);
        }
    }

    public Task<WriteResult> WriteAsync(TagDefinition tag, object? value, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            EnsureConnected();

            if (!S7AddressParser.TryParse(tag.Address, out var address))
            {
                return Task.FromResult(WriteResult.Fail($"Unsupported S7 address: {tag.Address}"));
            }

            try
            {
                WriteTypedValue(address, tag.DataType, value);
                return Task.FromResult(WriteResult.Ok());
            }
            catch (Exception ex)
            {
                return Task.FromResult(WriteResult.Fail(ex.Message));
            }
        }
    }

    public ConnectionState GetConnectionState()
    {
        lock (_syncRoot)
        {
            return _state;
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected)
        {
            throw new InvalidOperationException("S7 channel runtime is disconnected.");
        }
    }

    private void EnsureSeedMemory()
    {
        foreach (var tag in _context.TagsByNodeId.Values)
        {
            if (!S7AddressParser.TryParse(tag.Address, out var address))
            {
                continue;
            }

            var memory = GetDbMemory(address.DbNumber);
            EnsureRange(memory, address.ByteOffset, address.ByteLength);

            switch (tag.DataType)
            {
                case TagDataType.Boolean:
                    SetBit(memory, address.ByteOffset, address.BitOffset ?? 0, false);
                    break;
                case TagDataType.Int16:
                case TagDataType.UInt16:
                case TagDataType.Word:
                    WriteUInt16(memory, address.ByteOffset, 0);
                    break;
                case TagDataType.Int32:
                case TagDataType.UInt32:
                case TagDataType.DWord:
                    WriteInt32(memory, address.ByteOffset, 0);
                    break;
                case TagDataType.Float:
                    WriteFloat(memory, address.ByteOffset, 0f);
                    break;
                case TagDataType.Double:
                    WriteInt32(memory, address.ByteOffset, 0);
                    break;
                case TagDataType.String:
                    memory[address.ByteOffset] = 0;
                    break;
            }
        }
    }

    private object ReadTypedValue(S7Address address, TagDataType dataType)
    {
        var memory = GetDbMemory(address.DbNumber);
        EnsureRange(memory, address.ByteOffset, address.ByteLength);

        return dataType switch
        {
            TagDataType.Boolean => GetBit(memory, address.ByteOffset, address.BitOffset ?? 0),
            TagDataType.Int16 => unchecked((short)ReadUInt16(memory, address.ByteOffset)),
            TagDataType.UInt16 => ReadUInt16(memory, address.ByteOffset),
            TagDataType.Word => ReadUInt16(memory, address.ByteOffset),
            TagDataType.Int32 => ReadInt32(memory, address.ByteOffset),
            TagDataType.UInt32 => ReadUInt32(memory, address.ByteOffset),
            TagDataType.DWord => ReadUInt32(memory, address.ByteOffset),
            TagDataType.Float => ReadFloat(memory, address.ByteOffset),
            TagDataType.Double => ReadInt32(memory, address.ByteOffset),
            TagDataType.String => $"DB{address.DbNumber}:{memory[address.ByteOffset]}",
            _ => 0
        };
    }

    private void WriteTypedValue(S7Address address, TagDataType dataType, object? value)
    {
        var memory = GetDbMemory(address.DbNumber);
        EnsureRange(memory, address.ByteOffset, address.ByteLength);

        switch (dataType)
        {
            case TagDataType.Boolean:
                SetBit(memory, address.ByteOffset, address.BitOffset ?? 0, Convert.ToBoolean(value));
                return;
            case TagDataType.Int16:
                WriteUInt16(memory, address.ByteOffset, unchecked((ushort)Convert.ToInt16(value)));
                return;
            case TagDataType.UInt16:
                WriteUInt16(memory, address.ByteOffset, Convert.ToUInt16(value));
                return;
            case TagDataType.Word:
                WriteUInt16(memory, address.ByteOffset, Convert.ToUInt16(value));
                return;
            case TagDataType.Int32:
                WriteInt32(memory, address.ByteOffset, Convert.ToInt32(value));
                return;
            case TagDataType.UInt32:
                WriteUInt32(memory, address.ByteOffset, Convert.ToUInt32(value));
                return;
            case TagDataType.DWord:
                WriteUInt32(memory, address.ByteOffset, Convert.ToUInt32(value));
                return;
            case TagDataType.Float:
                WriteFloat(memory, address.ByteOffset, Convert.ToSingle(value));
                return;
            case TagDataType.Double:
                WriteInt32(memory, address.ByteOffset, Convert.ToInt32(Math.Round(Convert.ToDouble(value))));
                return;
            case TagDataType.String:
                memory[address.ByteOffset] = (byte)(value?.ToString()?.Length ?? 0);
                return;
            default:
                throw new NotSupportedException($"Unsupported tag type: {dataType}");
        }
    }

    private byte[] GetDbMemory(int dbNumber)
    {
        if (_dbMemory.TryGetValue(dbNumber, out var existing))
        {
            return existing;
        }

        var created = new byte[4_096];
        _dbMemory[dbNumber] = created;
        return created;
    }

    private static void EnsureRange(byte[] memory, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > memory.Length)
        {
            throw new InvalidOperationException($"S7 memory range out of bounds: offset={offset}, length={length}");
        }
    }

    private static int ReadInt32(byte[] memory, int offset)
    {
        EnsureRange(memory, offset, 4);
        return (memory[offset] << 24) |
               (memory[offset + 1] << 16) |
               (memory[offset + 2] << 8) |
               memory[offset + 3];
    }

    private static uint ReadUInt32(byte[] memory, int offset)
    {
        return unchecked((uint)ReadInt32(memory, offset));
    }

    private static ushort ReadUInt16(byte[] memory, int offset)
    {
        EnsureRange(memory, offset, 2);
        return (ushort)((memory[offset] << 8) | memory[offset + 1]);
    }

    private static float ReadFloat(byte[] memory, int offset)
    {
        EnsureRange(memory, offset, 4);
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = memory[offset];
        bytes[1] = memory[offset + 1];
        bytes[2] = memory[offset + 2];
        bytes[3] = memory[offset + 3];
        if (BitConverter.IsLittleEndian)
        {
            bytes.Reverse();
        }

        return BitConverter.ToSingle(bytes);
    }

    private static void WriteInt32(byte[] memory, int offset, int value)
    {
        EnsureRange(memory, offset, 4);
        memory[offset] = (byte)((value >> 24) & 0xFF);
        memory[offset + 1] = (byte)((value >> 16) & 0xFF);
        memory[offset + 2] = (byte)((value >> 8) & 0xFF);
        memory[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteUInt32(byte[] memory, int offset, uint value)
    {
        WriteInt32(memory, offset, unchecked((int)value));
    }

    private static void WriteUInt16(byte[] memory, int offset, ushort value)
    {
        EnsureRange(memory, offset, 2);
        memory[offset] = (byte)((value >> 8) & 0xFF);
        memory[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteFloat(byte[] memory, int offset, float value)
    {
        EnsureRange(memory, offset, 4);
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        memory[offset] = bytes[0];
        memory[offset + 1] = bytes[1];
        memory[offset + 2] = bytes[2];
        memory[offset + 3] = bytes[3];
    }

    private static bool GetBit(byte[] memory, int byteOffset, int bitOffset)
    {
        EnsureRange(memory, byteOffset, 1);
        var mask = (byte)(1 << bitOffset);
        return (memory[byteOffset] & mask) != 0;
    }

    private static void SetBit(byte[] memory, int byteOffset, int bitOffset, bool value)
    {
        EnsureRange(memory, byteOffset, 1);
        var mask = (byte)(1 << bitOffset);
        if (value)
        {
            memory[byteOffset] |= mask;
        }
        else
        {
            memory[byteOffset] &= (byte)~mask;
        }
    }
}
