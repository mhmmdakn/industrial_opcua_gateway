using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DriverGateway.LoadTests.ModbusTcp;

public sealed class LocalModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _acceptTask;
    private readonly ushort[] _holdingRegisters;
    private readonly ushort[] _inputRegisters;
    private readonly object _sync = new();

    private LocalModbusServer(int port, int addressSpaceSize)
    {
        _holdingRegisters = new ushort[addressSpaceSize];
        _inputRegisters = new ushort[addressSpaceSize];
        for (var index = 0; index < _inputRegisters.Length; index++)
        {
            _inputRegisters[index] = (ushort)(index % ushort.MaxValue);
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<LocalModbusServer?> TryStartAsync(ModbusLoadScenario scenario, CancellationToken cancellationToken)
    {
        if (!scenario.UseLocalServer)
        {
            return Task.FromResult<LocalModbusServer?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var target = EndpointParser.Parse(scenario.Endpoint);
        if (!string.Equals(target.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Local server mode requires a loopback endpoint. Current endpoint: {scenario.Endpoint}");
        }

        var server = new LocalModbusServer(target.Port, scenario.AddressSpaceSize);
        Console.WriteLine($"[loadtest] Local Modbus server started at 127.0.0.1:{server.Port}");
        return Task.FromResult<LocalModbusServer?>(server);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
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

                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected at shutdown.
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] header;
                try
                {
                    header = new byte[7];
                    await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                var unitId = header[6];

                if (protocolId != 0 || length < 2)
                {
                    break;
                }

                var requestPdu = new byte[length - 1];
                try
                {
                    await ReadExactAsync(stream, requestPdu, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                var responsePdu = HandleRequest(requestPdu);
                var frame = BuildFrame(transactionId, unitId, responsePdu);
                try
                {
                    await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private byte[] HandleRequest(byte[] requestPdu)
    {
        if (requestPdu.Length == 0)
        {
            return BuildException(0x00, 0x03);
        }

        var functionCode = requestPdu[0];
        return functionCode switch
        {
            0x03 => HandleReadRegisters(requestPdu, functionCode, _holdingRegisters),
            0x04 => HandleReadRegisters(requestPdu, functionCode, _inputRegisters),
            0x06 => HandleWriteSingleRegister(requestPdu),
            0x10 => HandleWriteMultipleRegisters(requestPdu),
            _ => BuildException(functionCode, 0x01)
        };
    }

    private byte[] HandleReadRegisters(byte[] requestPdu, byte functionCode, ushort[] source)
    {
        if (requestPdu.Length < 5)
        {
            return BuildException(functionCode, 0x03);
        }

        var start = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(3, 2));
        if (quantity is 0 or > 125)
        {
            return BuildException(functionCode, 0x03);
        }

        if (start + quantity > source.Length)
        {
            return BuildException(functionCode, 0x02);
        }

        var pdu = new byte[2 + (quantity * 2)];
        pdu[0] = functionCode;
        pdu[1] = (byte)(quantity * 2);

        lock (_sync)
        {
            for (var index = 0; index < quantity; index++)
            {
                var value = source[start + index];
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (index * 2), 2), value);
            }
        }

        return pdu;
    }

    private byte[] HandleWriteSingleRegister(byte[] requestPdu)
    {
        const byte functionCode = 0x06;
        if (requestPdu.Length < 5)
        {
            return BuildException(functionCode, 0x03);
        }

        var address = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(1, 2));
        var value = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(3, 2));
        if (address >= _holdingRegisters.Length)
        {
            return BuildException(functionCode, 0x02);
        }

        lock (_sync)
        {
            _holdingRegisters[address] = value;
        }

        var pdu = new byte[5];
        requestPdu.AsSpan(0, 5).CopyTo(pdu);
        return pdu;
    }

    private byte[] HandleWriteMultipleRegisters(byte[] requestPdu)
    {
        const byte functionCode = 0x10;
        if (requestPdu.Length < 6)
        {
            return BuildException(functionCode, 0x03);
        }

        var start = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(3, 2));
        var byteCount = requestPdu[5];

        if (quantity is 0 or > 123 || byteCount != quantity * 2)
        {
            return BuildException(functionCode, 0x03);
        }

        if (requestPdu.Length < 6 + byteCount)
        {
            return BuildException(functionCode, 0x03);
        }

        if (start + quantity > _holdingRegisters.Length)
        {
            return BuildException(functionCode, 0x02);
        }

        lock (_sync)
        {
            for (var index = 0; index < quantity; index++)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(requestPdu.AsSpan(6 + (index * 2), 2));
                _holdingRegisters[start + index] = value;
            }
        }

        var pdu = new byte[5];
        pdu[0] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), quantity);
        return pdu;
    }

    private static byte[] BuildException(byte functionCode, byte exceptionCode)
    {
        return [(byte)(functionCode | 0x80), exceptionCode];
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
                throw new IOException("Socket closed.");
            }

            totalRead += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected at shutdown.
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
