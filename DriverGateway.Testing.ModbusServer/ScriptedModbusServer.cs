using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DriverGateway.Testing.ModbusServer;

public sealed class ScriptedModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<ModbusRequest, ModbusServerResponse> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private readonly object _sync = new();
    private readonly List<ModbusRequest> _requests = [];

    private ScriptedModbusServer(TcpListener listener, Func<ModbusRequest, ModbusServerResponse> handler)
    {
        _listener = listener;
        _handler = handler;
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ModbusRequest? LastRequest
    {
        get
        {
            lock (_sync)
            {
                return _requests.Count == 0 ? null : _requests[^1];
            }
        }
    }

    public IReadOnlyList<ModbusRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public static Task<ScriptedModbusServer> StartAsync(Func<ModbusRequest, ModbusServerResponse> handler, int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        return Task.FromResult(new ScriptedModbusServer(listener, handler));
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        ModbusRequest request;
                        try
                        {
                            request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch
                        {
                            break;
                        }

                        lock (_sync)
                        {
                            _requests.Add(request);
                        }

                        var response = _handler(request);
                        var tx = unchecked((ushort)(response.TransactionId + response.TransactionIdOffset));
                        var unit = response.UnitIdOverride ?? request.UnitId;
                        var frame = BuildFrame(tx, unit, response.Pdu);
                        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private static async Task<ModbusRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[7];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

        var transactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        if (protocolId != 0)
        {
            throw new InvalidOperationException($"Invalid protocol id {protocolId}.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        var unitId = header[6];
        if (length < 2)
        {
            throw new InvalidOperationException($"Invalid MBAP length {length}.");
        }

        var pdu = new byte[length - 1];
        await ReadExactAsync(stream, pdu, cancellationToken).ConfigureAwait(false);
        var functionCode = pdu[0];

        ushort startAddress = 0;
        ushort quantity = 0;
        ushort[] registerValues = [];

        if ((functionCode is >= 0x01 and <= 0x06 or 0x10) && pdu.Length >= 5)
        {
            startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
            quantity = functionCode == 0x10
                ? BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2))
                : (ushort)1;

            if (functionCode is >= 0x01 and <= 0x04)
            {
                quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
            }

            if (functionCode == 0x10)
            {
                var byteCount = pdu[5];
                registerValues = new ushort[byteCount / 2];
                for (var index = 0; index < registerValues.Length; index++)
                {
                    registerValues[index] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + (index * 2), 2));
                }
            }
        }

        return new ModbusRequest(transactionId, unitId, functionCode, startAddress, quantity, registerValues, pdu);
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
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Socket closed.");
            }

            total += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _cts.Dispose();
    }
}

public sealed record ModbusRequest(
    ushort TransactionId,
    byte UnitId,
    byte FunctionCode,
    ushort StartAddress,
    ushort Quantity,
    ushort[] RegisterValues,
    byte[] Pdu);

public sealed record ModbusServerResponse(
    ushort TransactionId,
    byte? UnitIdOverride,
    short TransactionIdOffset,
    byte[] Pdu)
{
    public static ModbusServerResponse EchoWrite(ushort transactionId, byte unitId, byte[] pdu)
    {
        return new ModbusServerResponse(transactionId, unitId, 0, pdu);
    }

    public static ModbusServerResponse WriteMultipleRegistersAck(ushort transactionId, byte unitId, ushort startAddress, ushort quantity)
    {
        var pdu = new byte[5];
        pdu[0] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), quantity);
        return new ModbusServerResponse(transactionId, unitId, 0, pdu);
    }

    public static ModbusServerResponse ReadBits(ushort transactionId, byte unitId, byte functionCode, IReadOnlyList<bool> bits)
    {
        var byteCount = (bits.Count + 7) / 8;
        var pdu = new byte[2 + byteCount];
        pdu[0] = functionCode;
        pdu[1] = (byte)byteCount;

        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
            {
                pdu[2 + (index / 8)] |= (byte)(1 << (index % 8));
            }
        }

        return new ModbusServerResponse(transactionId, unitId, 0, pdu);
    }

    public static ModbusServerResponse ReadRegisters(
        ushort transactionId,
        byte unitId,
        byte functionCode,
        IReadOnlyList<ushort> registers,
        short transactionIdOffset = 0)
    {
        var pdu = new byte[2 + (registers.Count * 2)];
        pdu[0] = functionCode;
        pdu[1] = (byte)(registers.Count * 2);

        for (var index = 0; index < registers.Count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (index * 2), 2), registers[index]);
        }

        return new ModbusServerResponse(transactionId, unitId, transactionIdOffset, pdu);
    }

    public static ModbusServerResponse Exception(ushort transactionId, byte unitId, byte functionCode, byte exceptionCode)
    {
        return new ModbusServerResponse(transactionId, unitId, 0, [(byte)(functionCode | 0x80), exceptionCode]);
    }
}
