using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DriverGateway.Host.OpcUa.Runtime;
using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;
using DriverGateway.Core.Services;
using DriverGateway.Plugins.Abstractions;
using DriverGateway.Plugins.ModbusTcp;
using DriverGateway.Plugins.S7Comm;
using DriverGateway.Testing.ModbusServer;

var failures = new List<string>();

await Run("DemandTracker keeps one-shot demand until TTL", TestDemandTrackerAsync, failures);
await Run("S7 planner merges contiguous DB ranges", TestS7PlannerAsync, failures);
await Run("Modbus planner merges contiguous holding registers", TestModbusPlannerAsync, failures);
await Run("Modbus endpoint accepts host:port and modbus://host:port", TestModbusEndpointFormatsAsync, failures);
await Run("Modbus unitId validation applies defaults and range checks", TestModbusUnitIdValidationAsync, failures);
await Run("Modbus wordOrder affects Int32/Double register writes", TestModbusWordOrderWriteAsync, failures);
await Run("Modbus supports Word/DWord/Float read-write conversions", TestModbusAdditionalDataTypesAsync, failures);
await Run("Modbus String tags return Bad on read and Fail on write", TestModbusStringBehaviorAsync, failures);
await Run("Modbus exception response is surfaced to caller", TestModbusExceptionResponseAsync, failures);
await Run("Modbus MBAP transaction mismatch disconnects runtime", TestModbusTransactionMismatchAsync, failures);
await Run("Modbus write-only channel reconnects after server restart", TestModbusWriteOnlyChannelReconnectAsync, failures);
await Run("Modbus subscription channel reconnects after server restart", TestModbusSubscriptionChannelReconnectAsync, failures);
await Run("Modbus connects when server becomes available after startup", TestModbusConnectsWhenServerStartsLaterAsync, failures);

if (failures.Count == 0)
{
    Console.WriteLine("All smoke tests passed.");
    return;
}

Console.WriteLine("Smoke tests failed:");
foreach (var failure in failures)
{
    Console.WriteLine($"- {failure}");
}

Environment.ExitCode = 1;

static async Task Run(string name, Func<Task> test, ICollection<string> failures)
{
    try
    {
        await test().ConfigureAwait(false);
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"[FAIL] {name}");
    }
}

static Task TestDemandTrackerAsync()
{
    var tracker = new DemandTracker();
    tracker.RegisterOneShotDemand("Line1.Device1.Tag1", TimeSpan.FromMilliseconds(300));
    var activeNow = tracker.GetActiveDemand(DateTime.UtcNow);
    if (!activeNow.Contains("Line1.Device1.Tag1"))
    {
        throw new InvalidOperationException("One-shot demand was not active immediately.");
    }

    Thread.Sleep(400);
    var activeLater = tracker.GetActiveDemand(DateTime.UtcNow);
    if (activeLater.Contains("Line1.Device1.Tag1"))
    {
        throw new InvalidOperationException("One-shot demand did not expire.");
    }

    return Task.CompletedTask;
}

static Task TestS7PlannerAsync()
{
    var planner = new S7BatchPlanner(maxBlockBytes: 16);
    var tags = new[]
    {
        CreateTag("S7Ch1.PLC1.Tag1", "DB1.DBD0"),
        CreateTag("S7Ch1.PLC1.Tag2", "DB1.DBD4"),
        CreateTag("S7Ch1.PLC1.Tag3", "DB1.DBD8"),
        CreateTag("S7Ch1.PLC1.Tag4", "DB1.DBD32")
    };

    var batches = planner.BuildBatches(tags);
    if (batches.Count != 2)
    {
        throw new InvalidOperationException($"Expected 2 batches, found {batches.Count}.");
    }

    return Task.CompletedTask;
}

static Task TestModbusPlannerAsync()
{
    var planner = new ModbusBatchPlanner(maxRegisterSpan: 10);
    var tags = new[]
    {
        CreateTag("MbCh1.Dev.Tag1", "HR:0"),
        CreateTag("MbCh1.Dev.Tag2", "HR:1"),
        CreateTag("MbCh1.Dev.Tag3", "HR:2"),
        CreateTag("MbCh1.Dev.Tag4", "HR:20")
    };

    var batches = planner.BuildBatches(tags);
    if (batches.Count != 2)
    {
        throw new InvalidOperationException($"Expected 2 batches, found {batches.Count}.");
    }

    return Task.CompletedTask;
}

static async Task TestModbusEndpointFormatsAsync()
{
    await using var server = await ScriptedModbusServer.StartAsync(static request => request.FunctionCode switch
    {
        0x01 => ModbusServerResponse.ReadBits(request.TransactionId, request.UnitId, request.FunctionCode, [false]),
        _ => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)
    }).ConfigureAwait(false);

    var tag = CreateModbusTag("Ch1.Dev1.Coil", "COIL:0", TagDataType.Boolean);
    foreach (var endpoint in new[] { $"127.0.0.1:{server.Port}", $"modbus://127.0.0.1:{server.Port}" })
    {
        await using var runtime = CreateModbusRuntime(endpoint, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), [tag]);
        await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
        if (runtime.GetConnectionState() != ConnectionState.Connected)
        {
            throw new InvalidOperationException($"Runtime did not reach Connected for endpoint '{endpoint}'.");
        }
    }
}

static async Task TestModbusUnitIdValidationAsync()
{
    var tag = CreateModbusTag("Ch1.Dev1.Coil", "COIL:0", TagDataType.Boolean);

    await using (var invalidRuntime = CreateModbusRuntime(
                     "127.0.0.1:502",
                     new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["unitId"] = "0" },
                     [tag]))
    {
        await AssertThrowsAsync(
            () => invalidRuntime.StartAsync(CancellationToken.None),
            "unitId").ConfigureAwait(false);
    }

    await using var server = await ScriptedModbusServer.StartAsync(static request =>
        request.FunctionCode == 0x01
            ? ModbusServerResponse.ReadBits(request.TransactionId, request.UnitId, request.FunctionCode, [true])
            : ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)).ConfigureAwait(false);

    await using var runtime = CreateModbusRuntime(
        $"127.0.0.1:{server.Port}",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["unitId"] = "7" },
        [tag]);

    await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
    _ = await runtime.ReadAsync([tag], CancellationToken.None).ConfigureAwait(false);

    var last = server.LastRequest;
    if (last is null || last.UnitId != 7)
    {
        throw new InvalidOperationException("Modbus unitId setting was not applied to outgoing requests.");
    }
}

static async Task TestModbusWordOrderWriteAsync()
{
    var intTag = CreateModbusTag("Ch1.Dev1.IntVal", "HR:10", TagDataType.Int32);
    var doubleTag = CreateModbusTag("Ch1.Dev1.DoubleVal", "HR:20", TagDataType.Double);
    var intValue = 0x11223344;
    var doubleValue = 12.5d;

    foreach (var wordOrder in new[] { "high-word-first", "low-word-first" })
    {
        await using var server = await ScriptedModbusServer.StartAsync(static request =>
        {
            if (request.FunctionCode == 0x10)
            {
                return ModbusServerResponse.WriteMultipleRegistersAck(request.TransactionId, request.UnitId, request.StartAddress, request.Quantity);
            }

            return ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu);
        }).ConfigureAwait(false);

        await using var runtime = CreateModbusRuntime(
            $"127.0.0.1:{server.Port}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["wordOrder"] = wordOrder },
            [intTag, doubleTag]);

        await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var writeInt = await runtime.WriteAsync(intTag, intValue, CancellationToken.None).ConfigureAwait(false);
        if (!writeInt.Success)
        {
            throw new InvalidOperationException($"Int32 write failed for wordOrder '{wordOrder}': {writeInt.Error}");
        }

        var writeDouble = await runtime.WriteAsync(doubleTag, doubleValue, CancellationToken.None).ConfigureAwait(false);
        if (!writeDouble.Success)
        {
            throw new InvalidOperationException($"Double write failed for wordOrder '{wordOrder}': {writeDouble.Error}");
        }

        var writes = server.Requests.Where(static r => r.FunctionCode == 0x10).ToArray();
        if (writes.Length != 2)
        {
            throw new InvalidOperationException($"Expected 2 FC16 requests for wordOrder '{wordOrder}', found {writes.Length}.");
        }

        var intWrite = writes.Single(w => w.StartAddress == 10);
        var doubleWrite = writes.Single(w => w.StartAddress == 20);

        var expectedIntWords = wordOrder == "low-word-first"
            ? new ushort[] { 0x3344, 0x1122 }
            : new ushort[] { 0x1122, 0x3344 };

        var highDoubleWords = BuildHighWordDouble(doubleValue);
        var expectedDoubleWords = wordOrder == "low-word-first"
            ? highDoubleWords.Reverse().ToArray()
            : highDoubleWords;

        AssertSequence(intWrite.RegisterValues, expectedIntWords, $"Unexpected Int32 words for wordOrder '{wordOrder}'.");
        AssertSequence(doubleWrite.RegisterValues, expectedDoubleWords, $"Unexpected Double words for wordOrder '{wordOrder}'.");
    }
}

static async Task TestModbusAdditionalDataTypesAsync()
{
    await using var server = await ScriptedModbusServer.StartAsync(request =>
    {
        if (request.FunctionCode == 0x03)
        {
            return request.StartAddress switch
            {
                30 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [0x1234]),
                40 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [0x89AB, 0xCDEF]),
                50 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [0x3FC0, 0x0000]),
                _ => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [0x0000])
            };
        }

        if (request.FunctionCode == 0x06)
        {
            return ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu);
        }

        if (request.FunctionCode == 0x10)
        {
            return ModbusServerResponse.WriteMultipleRegistersAck(
                request.TransactionId,
                request.UnitId,
                request.StartAddress,
                request.Quantity);
        }

        return ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu);
    }).ConfigureAwait(false);

    var wordTag = CreateModbusTag("Ch1.Dev1.WordVal", "HR:30", TagDataType.Word);
    var dwordTag = CreateModbusTag("Ch1.Dev1.DWordVal", "HR:40", TagDataType.DWord);
    var floatTag = CreateModbusTag("Ch1.Dev1.FloatVal", "HR:50", TagDataType.Float);

    await using var runtime = CreateModbusRuntime(
        $"127.0.0.1:{server.Port}",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [wordTag, dwordTag, floatTag]);

    await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

    var wordWrite = await runtime.WriteAsync(wordTag, (ushort)0xABCD, CancellationToken.None).ConfigureAwait(false);
    var dwordWrite = await runtime.WriteAsync(dwordTag, 0x89ABCDEFu, CancellationToken.None).ConfigureAwait(false);
    var floatWrite = await runtime.WriteAsync(floatTag, 1.5f, CancellationToken.None).ConfigureAwait(false);
    if (!wordWrite.Success || !dwordWrite.Success || !floatWrite.Success)
    {
        throw new InvalidOperationException("At least one additional data type write failed.");
    }

    var writes = server.Requests.ToArray();
    var fc06 = writes.Single(request => request.FunctionCode == 0x06 && request.StartAddress == 30);
    if (fc06.Pdu.Length < 5 || BinaryPrimitives.ReadUInt16BigEndian(fc06.Pdu.AsSpan(3, 2)) != 0xABCD)
    {
        throw new InvalidOperationException("Word write payload mismatch.");
    }

    var dwordFc16 = writes.Single(request => request.FunctionCode == 0x10 && request.StartAddress == 40);
    AssertSequence(dwordFc16.RegisterValues, [0x89AB, 0xCDEF], "DWord write register words mismatch.");

    var floatFc16 = writes.Single(request => request.FunctionCode == 0x10 && request.StartAddress == 50);
    AssertSequence(floatFc16.RegisterValues, [0x3FC0, 0x0000], "Float write register words mismatch.");

    var readResults = await runtime.ReadAsync([wordTag, dwordTag, floatTag], CancellationToken.None).ConfigureAwait(false);
    var wordRead = readResults.Single(result => result.NodeIdentifier == wordTag.NodeIdentifier).Value;
    var dwordRead = readResults.Single(result => result.NodeIdentifier == dwordTag.NodeIdentifier).Value;
    var floatRead = readResults.Single(result => result.NodeIdentifier == floatTag.NodeIdentifier).Value;

    if (Convert.ToUInt16(wordRead) != 0x1234)
    {
        throw new InvalidOperationException("Word read conversion mismatch.");
    }

    if (Convert.ToUInt32(dwordRead) != 0x89ABCDEF)
    {
        throw new InvalidOperationException("DWord read conversion mismatch.");
    }

    if (Math.Abs(Convert.ToSingle(floatRead) - 1.5f) > 0.0001f)
    {
        throw new InvalidOperationException("Float read conversion mismatch.");
    }
}

static async Task TestModbusStringBehaviorAsync()
{
    await using var server = await ScriptedModbusServer.StartAsync(static request =>
        ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)).ConfigureAwait(false);

    var stringTag = CreateModbusTag("Ch1.Dev1.Name", "HR:0", TagDataType.String);
    await using var runtime = CreateModbusRuntime(
        $"127.0.0.1:{server.Port}",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [stringTag]);

    await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

    var read = await runtime.ReadAsync([stringTag], CancellationToken.None).ConfigureAwait(false);
    if (read.Count != 1 || read.First().Quality != "Bad")
    {
        throw new InvalidOperationException("String read was expected to return Quality=Bad.");
    }

    var write = await runtime.WriteAsync(stringTag, "abc", CancellationToken.None).ConfigureAwait(false);
    if (write.Success)
    {
        throw new InvalidOperationException("String write was expected to fail.");
    }
}

static async Task TestModbusExceptionResponseAsync()
{
    await using var server = await ScriptedModbusServer.StartAsync(static request =>
        request.FunctionCode == 0x03
            ? ModbusServerResponse.Exception(request.TransactionId, request.UnitId, request.FunctionCode, 0x02)
            : ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)).ConfigureAwait(false);

    var tag = CreateModbusTag("Ch1.Dev1.IntVal", "HR:0", TagDataType.Int32);
    await using var runtime = CreateModbusRuntime(
        $"127.0.0.1:{server.Port}",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [tag]);

    await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

    await AssertThrowsAsync(
        async () =>
        {
            _ = await runtime.ReadAsync([tag], CancellationToken.None).ConfigureAwait(false);
        },
        "Illegal Data Address").ConfigureAwait(false);
}

static async Task TestModbusTransactionMismatchAsync()
{
    await using var server = await ScriptedModbusServer.StartAsync(static request =>
        request.FunctionCode == 0x03
            ? ModbusServerResponse.ReadRegisters(
                request.TransactionId,
                request.UnitId,
                request.FunctionCode,
                [0x0001, 0x0002],
                transactionIdOffset: 1)
            : ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)).ConfigureAwait(false);

    var tag = CreateModbusTag("Ch1.Dev1.IntVal", "HR:0", TagDataType.Int32);
    await using var runtime = CreateModbusRuntime(
        $"127.0.0.1:{server.Port}",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [tag]);

    await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

    await AssertThrowsAsync(
        async () =>
        {
            _ = await runtime.ReadAsync([tag], CancellationToken.None).ConfigureAwait(false);
        },
        "Transaction mismatch").ConfigureAwait(false);

    if (runtime.GetConnectionState() != ConnectionState.Disconnected)
    {
        throw new InvalidOperationException("Runtime should move to Disconnected after MBAP transaction mismatch.");
    }
}

static async Task TestModbusWriteOnlyChannelReconnectAsync()
{
    var port = GetFreeTcpPort();
    var tag = CreateModbusTag("Drv1.MbCh1.Dev1.Setpoint", "HR:10", TagDataType.Int16);
    var tagsByNodeId = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        [tag.NodeIdentifier] = tag
    };
    var scanClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["default"] = 1_000 };
    var channel = new ChannelDefinition(
        Name: "MbCh1",
        Endpoint: $"127.0.0.1:{port}",
        Settings: TagDefinition.EmptySettings,
        RetryPolicy: new RetryPolicyOptions(InitialDelayMs: 200, MaxDelayMs: 1_000, JitterFactor: 0, HealthCheckIntervalMs: 60_000),
        ScanClasses: scanClasses,
        Devices: [new DeviceDefinition("Dev1", TagDefinition.EmptySettings, [tag])]);
    var driver = new DriverDefinition(
        Name: "Drv1",
        DriverType: ModbusTcpDriverPlugin.TypeName,
        Settings: TagDefinition.EmptySettings,
        Channels: [channel]);
    var runtimeContext = new ChannelRuntimeContext(driver, channel, tagsByNodeId, null);
    var runtime = new ModbusTcpDriverPlugin().CreateChannelRuntime(runtimeContext);

    await using var worker = new ChannelWorker(
        channel,
        tagsByNodeId,
        runtime,
        new DemandTracker(),
        new InMemoryTagValueCache(),
        null);

    ScriptedModbusServer? server = null;
    try
    {
        server = await ScriptedModbusServer.StartAsync(static request => request.FunctionCode switch
        {
            0x06 => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu),
            0x10 => ModbusServerResponse.WriteMultipleRegistersAck(request.TransactionId, request.UnitId, request.StartAddress, request.Quantity),
            0x03 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [0x0001]),
            _ => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)
        }, port).ConfigureAwait(false);

        worker.Start();
        await WaitUntilAsync(
            static state => state == ConnectionState.Connected,
            worker.GetConnectionState,
            TimeSpan.FromSeconds(3),
            "Worker failed to establish initial Modbus connection.").ConfigureAwait(false);

        var firstWrite = await worker.WriteImmediateAsync(tag, (short)11, CancellationToken.None).ConfigureAwait(false);
        if (!firstWrite.Success)
        {
            throw new InvalidOperationException($"First write failed unexpectedly: {firstWrite.Error}");
        }

        await server.DisposeAsync().ConfigureAwait(false);
        server = null;
        await Task.Delay(300).ConfigureAwait(false);

        var disconnectedWrite = await worker.WriteImmediateAsync(tag, (short)12, CancellationToken.None).ConfigureAwait(false);
        if (disconnectedWrite.Success)
        {
            throw new InvalidOperationException("Write should fail while server is down.");
        }

        server = await ScriptedModbusServer.StartAsync(static request => request.FunctionCode switch
        {
            0x06 => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu),
            0x10 => ModbusServerResponse.WriteMultipleRegistersAck(request.TransactionId, request.UnitId, request.StartAddress, request.Quantity),
            0x03 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [0x0001]),
            _ => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)
        }, port).ConfigureAwait(false);

        WriteResult recovered = WriteResult.Fail("Reconnect write was not attempted.");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            recovered = await worker.WriteImmediateAsync(tag, (short)(100 + attempt), CancellationToken.None).ConfigureAwait(false);
            if (recovered.Success)
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        if (!recovered.Success)
        {
            throw new InvalidOperationException($"Write did not recover after server restart. Last error: {recovered.Error}");
        }
    }
    finally
    {
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}

static async Task TestModbusSubscriptionChannelReconnectAsync()
{
    var port = GetFreeTcpPort();
    var tag = CreateModbusTag("Drv1.MbCh1.Dev1.Level", "HR:10", TagDataType.Int16);
    var tagsByNodeId = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        [tag.NodeIdentifier] = tag
    };
    var scanClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["default"] = 100 };
    var channel = new ChannelDefinition(
        Name: "MbCh1",
        Endpoint: $"127.0.0.1:{port}",
        Settings: TagDefinition.EmptySettings,
        RetryPolicy: new RetryPolicyOptions(InitialDelayMs: 100, MaxDelayMs: 800, JitterFactor: 0, HealthCheckIntervalMs: 60_000),
        ScanClasses: scanClasses,
        Devices: [new DeviceDefinition("Dev1", TagDefinition.EmptySettings, [tag])]);
    var driver = new DriverDefinition(
        Name: "Drv1",
        DriverType: ModbusTcpDriverPlugin.TypeName,
        Settings: TagDefinition.EmptySettings,
        Channels: [channel]);
    var runtimeContext = new ChannelRuntimeContext(driver, channel, tagsByNodeId, null);
    var runtime = new ModbusTcpDriverPlugin().CreateChannelRuntime(runtimeContext);
    var demandTracker = new DemandTracker();
    var cache = new InMemoryTagValueCache();

    await using var worker = new ChannelWorker(
        channel,
        tagsByNodeId,
        runtime,
        demandTracker,
        cache,
        null);

    ScriptedModbusServer? server = null;
    try
    {
        server = await ScriptedModbusServer.StartAsync(static request => request.FunctionCode switch
        {
            0x03 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [11]),
            0x06 => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu),
            0x10 => ModbusServerResponse.WriteMultipleRegistersAck(request.TransactionId, request.UnitId, request.StartAddress, request.Quantity),
            _ => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)
        }, port).ConfigureAwait(false);

        demandTracker.AddSubscriptionDemand(tag.NodeIdentifier);
        worker.Start();

        await WaitUntilAsync(
            static state => state == ConnectionState.Connected,
            worker.GetConnectionState,
            TimeSpan.FromSeconds(3),
            "Subscription worker failed to establish initial Modbus connection.").ConfigureAwait(false);

        await WaitUntilAsync(
            static sample => sample.IsOk && sample.Value == 11,
            () => ReadCachedInt16(cache, tag.NodeIdentifier),
            TimeSpan.FromSeconds(3),
            "Subscription cache did not receive initial value 11.").ConfigureAwait(false);

        await server.DisposeAsync().ConfigureAwait(false);
        server = null;

        await WaitUntilAsync(
            static state => state == ConnectionState.Disconnected,
            worker.GetConnectionState,
            TimeSpan.FromSeconds(4),
            "Worker did not transition to Disconnected after server shutdown.").ConfigureAwait(false);

        await Task.Delay(150).ConfigureAwait(false);

        server = await ScriptedModbusServer.StartAsync(static request => request.FunctionCode switch
        {
            0x03 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [22]),
            0x06 => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu),
            0x10 => ModbusServerResponse.WriteMultipleRegistersAck(request.TransactionId, request.UnitId, request.StartAddress, request.Quantity),
            _ => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)
        }, port).ConfigureAwait(false);

        await WaitUntilAsync(
            static state => state == ConnectionState.Connected,
            worker.GetConnectionState,
            TimeSpan.FromSeconds(5),
            "Worker did not reconnect after subscription server restart.").ConfigureAwait(false);

        await WaitUntilAsync(
            static sample => sample.IsOk && sample.Value == 22,
            () => ReadCachedInt16(cache, tag.NodeIdentifier),
            TimeSpan.FromSeconds(5),
            "Subscription cache did not update to post-reconnect value 22.").ConfigureAwait(false);
    }
    finally
    {
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}

static async Task TestModbusConnectsWhenServerStartsLaterAsync()
{
    var port = GetFreeTcpPort();
    var tag = CreateModbusTag("Drv1.MbCh1.Dev1.StartupReconnect", "HR:12", TagDataType.Int16);
    var tagsByNodeId = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        [tag.NodeIdentifier] = tag
    };
    var scanClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["default"] = 100 };
    var channel = new ChannelDefinition(
        Name: "MbCh1",
        Endpoint: $"127.0.0.1:{port}",
        Settings: TagDefinition.EmptySettings,
        RetryPolicy: new RetryPolicyOptions(InitialDelayMs: 100, MaxDelayMs: 800, JitterFactor: 0, HealthCheckIntervalMs: 60_000),
        ScanClasses: scanClasses,
        Devices: [new DeviceDefinition("Dev1", TagDefinition.EmptySettings, [tag])]);
    var driver = new DriverDefinition(
        Name: "Drv1",
        DriverType: ModbusTcpDriverPlugin.TypeName,
        Settings: TagDefinition.EmptySettings,
        Channels: [channel]);
    var runtimeContext = new ChannelRuntimeContext(driver, channel, tagsByNodeId, null);
    var runtime = new ModbusTcpDriverPlugin().CreateChannelRuntime(runtimeContext);
    var demandTracker = new DemandTracker();
    var cache = new InMemoryTagValueCache();

    await using var worker = new ChannelWorker(
        channel,
        tagsByNodeId,
        runtime,
        demandTracker,
        cache,
        null);

    demandTracker.AddSubscriptionDemand(tag.NodeIdentifier);
    worker.Start();

    await Task.Delay(600).ConfigureAwait(false);
    if (worker.GetConnectionState() == ConnectionState.Connected)
    {
        throw new InvalidOperationException("Worker should not be Connected while server is still down.");
    }

    await using var server = await ScriptedModbusServer.StartAsync(static request => request.FunctionCode switch
    {
        0x03 => ModbusServerResponse.ReadRegisters(request.TransactionId, request.UnitId, request.FunctionCode, [33]),
        0x06 => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu),
        0x10 => ModbusServerResponse.WriteMultipleRegistersAck(request.TransactionId, request.UnitId, request.StartAddress, request.Quantity),
        _ => ModbusServerResponse.EchoWrite(request.TransactionId, request.UnitId, request.Pdu)
    }, port).ConfigureAwait(false);

    await WaitUntilAsync(
        static state => state == ConnectionState.Connected,
        worker.GetConnectionState,
        TimeSpan.FromSeconds(5),
        "Worker did not connect after server became available.").ConfigureAwait(false);

    await WaitUntilAsync(
        static sample => sample.IsOk && sample.Value == 33,
        () => ReadCachedInt16(cache, tag.NodeIdentifier),
        TimeSpan.FromSeconds(5),
        "Subscription cache did not update after delayed server startup.").ConfigureAwait(false);
}

static async Task AssertThrowsAsync(Func<Task> action, string expectedFragment)
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        if (ContainsMessage(ex, expectedFragment))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception containing '{expectedFragment}', but got '{ex.Message}'.", ex);
    }

    throw new InvalidOperationException($"Expected exception containing '{expectedFragment}', but no exception was thrown.");
}

static bool ContainsMessage(Exception ex, string expectedFragment)
{
    var current = ex;
    while (current is not null)
    {
        if (current.Message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        current = current.InnerException!;
    }

    return false;
}

static void AssertSequence(IReadOnlyList<ushort> actual, IReadOnlyList<ushort> expected, string message)
{
    if (actual.Count != expected.Count)
    {
        throw new InvalidOperationException($"{message} Count mismatch: expected {expected.Count}, got {actual.Count}.");
    }

    for (var index = 0; index < actual.Count; index++)
    {
        if (actual[index] != expected[index])
        {
            throw new InvalidOperationException(
                $"{message} Index {index}: expected 0x{expected[index]:X4}, got 0x{actual[index]:X4}.");
        }
    }
}

static ushort[] BuildHighWordDouble(double value)
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

    return words;
}

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task WaitUntilAsync<T>(
    Func<T, bool> predicate,
    Func<T> sample,
    TimeSpan timeout,
    string failureMessage)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (predicate(sample()))
        {
            return;
        }

        await Task.Delay(100).ConfigureAwait(false);
    }

    throw new InvalidOperationException(failureMessage);
}

static (bool IsOk, short Value) ReadCachedInt16(ITagValueCache cache, string nodeIdentifier)
{
    if (!cache.TryGet(nodeIdentifier, out var snapshot) || snapshot.Value is null)
    {
        return (false, default);
    }

    try
    {
        return (true, Convert.ToInt16(snapshot.Value));
    }
    catch
    {
        return (false, default);
    }
}

static IChannelRuntime CreateModbusRuntime(
    string endpoint,
    IReadOnlyDictionary<string, string> channelSettings,
    IReadOnlyCollection<TagDefinition> tags)
{
    var settings = new Dictionary<string, string>(channelSettings, StringComparer.OrdinalIgnoreCase);
    var scanClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["default"] = 1000 };

    var device = new DeviceDefinition("Dev1", TagDefinition.EmptySettings, tags.ToArray());
    var channel = new ChannelDefinition(
        Name: "Ch1",
        Endpoint: endpoint,
        Settings: settings,
        RetryPolicy: new RetryPolicyOptions(),
        ScanClasses: scanClasses,
        Devices: [device]);

    var driver = new DriverDefinition(
        Name: "Drv1",
        DriverType: ModbusTcpDriverPlugin.TypeName,
        Settings: TagDefinition.EmptySettings,
        Channels: [channel]);

    var tagsByNodeId = tags.ToDictionary(static tag => tag.NodeIdentifier, StringComparer.OrdinalIgnoreCase);
    var context = new ChannelRuntimeContext(driver, channel, tagsByNodeId, null);
    return new ModbusTcpDriverPlugin().CreateChannelRuntime(context);
}

static TagDefinition CreateTag(string nodeIdentifier, string address)
{
    return new TagDefinition(
        DriverName: "Test",
        DriverType: "test",
        ChannelName: "Ch1",
        DeviceName: "Dev1",
        TagName: nodeIdentifier.Split('.').Last(),
        NodeIdentifier: nodeIdentifier,
        Address: address,
        DataType: TagDataType.Int32,
        ScanClass: "default",
        WriteMode: WriteMode.Immediate,
        Settings: TagDefinition.EmptySettings);
}

static TagDefinition CreateModbusTag(string nodeIdentifier, string address, TagDataType dataType)
{
    return new TagDefinition(
        DriverName: "Drv1",
        DriverType: ModbusTcpDriverPlugin.TypeName,
        ChannelName: "Ch1",
        DeviceName: "Dev1",
        TagName: nodeIdentifier.Split('.').Last(),
        NodeIdentifier: nodeIdentifier,
        Address: address,
        DataType: dataType,
        ScanClass: "default",
        WriteMode: WriteMode.Immediate,
        Settings: TagDefinition.EmptySettings);
}
