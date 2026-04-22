using System.Globalization;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

internal static class Program
{
    private static readonly object ReconnectSyncRoot = new();

    private static Session? _session;
    private static SessionReconnectHandler? _reconnectHandler;

    private static readonly NodeId TemperatureNode = NodeId.Parse("ns=2;s=Demo.Temperature");
    private static readonly NodeId PressureNode = NodeId.Parse("ns=2;s=Demo.Pressure");
    private static readonly NodeId MachineRunningNode = NodeId.Parse("ns=2;s=Demo.MachineRunning");

    public static async Task Main(string[] args)
    {
        var options = ClientOptions.Parse(args);
        var autoAcceptUntrusted = BuildMode.IsDebug;

        var config = await CreateClientConfigurationAsync(autoAcceptUntrusted);
        var application = new ApplicationInstance
        {
            ApplicationName = "OpcUaSampleClient",
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = config
        };

        var hasCertificate = await application.CheckApplicationInstanceCertificate(false, 2048);
        if (!hasCertificate)
        {
            throw new InvalidOperationException("Client application certificate could not be created or loaded.");
        }

        var endpointDescription = CoreClientUtils.SelectEndpoint(options.Endpoint, true, 15000);
        EnsureEndpointSecurity(endpointDescription);

        var endpointConfiguration = EndpointConfiguration.Create(config);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration)
        {
            UpdateBeforeConnect = true
        };

        _session = await Session.Create(
            config,
            configuredEndpoint,
            updateBeforeConnect: false,
            sessionName: "OpcUaSampleClientSession",
            sessionTimeout: 60000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null);

        _session.KeepAlive += SessionOnKeepAlive;

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Connected to {_session.Endpoint.EndpointUrl}");
        Console.WriteLine("Security: Basic256Sha256 + SignAndEncrypt");
        Console.WriteLine($"Trust Mode: {(autoAcceptUntrusted ? "Debug auto-trust" : "Release manual trust")}");

        if (options.Read || !options.HasAnyAction)
        {
            ReadCoreNodes(_session);
        }

        if (!string.IsNullOrWhiteSpace(options.WriteNode) && !string.IsNullOrWhiteSpace(options.WriteValue))
        {
            WriteSingleNode(_session, NodeId.Parse(options.WriteNode), options.WriteValue);
        }
        else if (!options.HasAnyAction)
        {
            WriteSampleValues(_session);
        }

        if (options.Subscribe || !options.HasAnyAction)
        {
            using var subscription = CreateSubscription(_session);
            Console.WriteLine("Subscription active. Press Ctrl+C to exit.");

            using var waitHandle = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                waitHandle.Set();
            };

            waitHandle.Wait();
        }

        CloseSession();
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Client stopped.");
    }

    private static async Task<ApplicationConfiguration> CreateClientConfigurationAsync(bool autoAcceptUntrusted)
    {
        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki", "client");

        var config = new ApplicationConfiguration
        {
            ApplicationName = "OpcUaSampleClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{Utils.GetHostName()}:OpcUaSampleClient",
            ProductUri = "urn:opcua:sample:client",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=OpcUaSampleClient, O=OPC_UA"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "trusted")
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "issuers")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = autoAcceptUntrusted,
                AddAppCertToTrustedStore = true
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000,
                MaxStringLength = 1_048_576,
                MaxByteStringLength = 1_048_576,
                MaxMessageSize = 4_194_304,
                MaxBufferSize = 65_535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000,
                MinSubscriptionLifetime = 10000
            },
            TraceConfiguration = new TraceConfiguration()
        };

        await config.Validate(ApplicationType.Client);

        config.CertificateValidator.CertificateValidation += (_, eventArgs) =>
        {
            if (eventArgs.Error.StatusCode == StatusCodes.BadCertificateUntrusted && autoAcceptUntrusted)
            {
                eventArgs.Accept = true;
                Console.WriteLine($"[CERT] Auto-accepted untrusted certificate: {eventArgs.Certificate.Subject}");
            }
        };

        return config;
    }

    private static void EnsureEndpointSecurity(EndpointDescription endpoint)
    {
        if (endpoint.SecurityMode != MessageSecurityMode.SignAndEncrypt || endpoint.SecurityPolicyUri != SecurityPolicies.Basic256Sha256)
        {
            throw new InvalidOperationException(
                $"Endpoint is not using required security. Mode={endpoint.SecurityMode}, Policy={endpoint.SecurityPolicyUri}");
        }
    }

    private static void ReadCoreNodes(Session session)
    {
        var nodesToRead = new ReadValueIdCollection
        {
            new() { NodeId = TemperatureNode, AttributeId = Attributes.Value },
            new() { NodeId = PressureNode, AttributeId = Attributes.Value },
            new() { NodeId = MachineRunningNode, AttributeId = Attributes.Value }
        };

        session.Read(
            null,
            maxAge: 0,
            timestampsToReturn: TimestampsToReturn.Both,
            nodesToRead,
            out var results,
            out var diagnosticInfos);

        ClientBase.ValidateResponse(results, nodesToRead);
        ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToRead);

        Console.WriteLine("Initial Read:");
        Console.WriteLine($"  {TemperatureNode}: {results[0].Value}");
        Console.WriteLine($"  {PressureNode}: {results[1].Value}");
        Console.WriteLine($"  {MachineRunningNode}: {results[2].Value}");
    }

    private static void WriteSampleValues(Session session)
    {
        var nextTemperature = Math.Round(20d + Random.Shared.NextDouble() * 10d, 2);
        var machineRunning = Random.Shared.Next(0, 2) == 1;

        var writes = new WriteValueCollection
        {
            new()
            {
                NodeId = TemperatureNode,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(nextTemperature))
            },
            new()
            {
                NodeId = MachineRunningNode,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(machineRunning))
            }
        };

        session.Write(null, writes, out var results, out var diagnosticInfos);
        ClientBase.ValidateResponse(results, writes);
        ClientBase.ValidateDiagnosticInfos(diagnosticInfos, writes);

        Console.WriteLine("Sample Write:");
        Console.WriteLine($"  {TemperatureNode} <= {nextTemperature.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  {MachineRunningNode} <= {machineRunning}");
    }

    private static void WriteSingleNode(Session session, NodeId nodeId, string rawValue)
    {
        var parsedValue = ParseValue(rawValue);
        var writes = new WriteValueCollection
        {
            new()
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(parsedValue))
            }
        };

        session.Write(null, writes, out var results, out var diagnosticInfos);
        ClientBase.ValidateResponse(results, writes);
        ClientBase.ValidateDiagnosticInfos(diagnosticInfos, writes);

        Console.WriteLine($"Write: {nodeId} <= {parsedValue}");
    }

    private static object ParseValue(string rawValue)
    {
        if (bool.TryParse(rawValue, out var boolValue))
        {
            return boolValue;
        }

        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue;
        }

        return rawValue;
    }

    private static Subscription CreateSubscription(Session session)
    {
        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = "DemoNodeSubscription",
            PublishingInterval = 1000,
            KeepAliveCount = 10,
            LifetimeCount = 60,
            MaxNotificationsPerPublish = 100,
            PublishingEnabled = true,
            Priority = 100
        };

        subscription.AddItem(CreateMonitoredItem(TemperatureNode));
        subscription.AddItem(CreateMonitoredItem(PressureNode));
        subscription.AddItem(CreateMonitoredItem(MachineRunningNode));

        session.AddSubscription(subscription);
        subscription.Create();

        return subscription;
    }

    private static MonitoredItem CreateMonitoredItem(NodeId nodeId)
    {
        var monitoredItem = new MonitoredItem
        {
            DisplayName = nodeId.ToString(),
            StartNodeId = nodeId,
            AttributeId = Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,
            SamplingInterval = 1000,
            QueueSize = 10,
            DiscardOldest = true
        };

        monitoredItem.Notification += (_, _) =>
        {
            foreach (var value in monitoredItem.DequeueValues())
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {nodeId} => {value.Value}");
            }
        };

        return monitoredItem;
    }

    private static void SessionOnKeepAlive(ISession session, KeepAliveEventArgs eventArgs)
    {
        if (ServiceResult.IsGood(eventArgs.Status))
        {
            return;
        }

        lock (ReconnectSyncRoot)
        {
            if (_reconnectHandler is not null)
            {
                return;
            }

            Console.WriteLine($"[WARN] KeepAlive status bad ({eventArgs.Status}). Reconnect started...");
            _reconnectHandler = new SessionReconnectHandler();
            _reconnectHandler.BeginReconnect(session, 5000, ClientReconnectComplete);
        }
    }

    private static void ClientReconnectComplete(object? sender, EventArgs e)
    {
        lock (ReconnectSyncRoot)
        {
            if (!ReferenceEquals(sender, _reconnectHandler) || _reconnectHandler is null)
            {
                return;
            }

            _session = (Session)_reconnectHandler.Session;
            _session.KeepAlive += SessionOnKeepAlive;

            Console.WriteLine($"[INFO] Reconnected at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            _reconnectHandler.Dispose();
            _reconnectHandler = null;
        }
    }

    private static void CloseSession()
    {
        lock (ReconnectSyncRoot)
        {
            _reconnectHandler?.Dispose();
            _reconnectHandler = null;

            if (_session is null)
            {
                return;
            }

            _session.KeepAlive -= SessionOnKeepAlive;
            _session.Close();
            _session.Dispose();
            _session = null;
        }
    }
}

internal sealed record ClientOptions(
    string Endpoint,
    bool Read,
    bool Subscribe,
    string? WriteNode,
    string? WriteValue)
{
    public bool HasExplicitWrite => !string.IsNullOrWhiteSpace(WriteNode) || !string.IsNullOrWhiteSpace(WriteValue);

    public bool HasAnyAction => Read || Subscribe || HasExplicitWrite;

    public static ClientOptions Parse(string[] args)
    {
        var endpoint = "opc.tcp://localhost:4840/UA/SampleServer";
        var read = false;
        var subscribe = false;
        string? writeNode = null;
        string? writeValue = null;

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--endpoint" when index + 1 < args.Length:
                    endpoint = args[++index];
                    break;
                case "--read":
                    read = true;
                    break;
                case "--subscribe":
                    subscribe = true;
                    break;
                case "--write-node" when index + 1 < args.Length:
                    writeNode = args[++index];
                    break;
                case "--write-value" when index + 1 < args.Length:
                    writeValue = args[++index];
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {current}");
            }
        }

        if ((writeNode is null) != (writeValue is null))
        {
            throw new ArgumentException("--write-node and --write-value must be provided together.");
        }

        return new ClientOptions(endpoint, read, subscribe, writeNode, writeValue);
    }

    private static void PrintHelpAndExit()
    {
        Console.WriteLine("OpcUaClientApp options:");
        Console.WriteLine("  --endpoint <opc.tcp://host:port/path>   Optional endpoint URL");
        Console.WriteLine("  --read                                  Read demo nodes");
        Console.WriteLine("  --subscribe                             Subscribe to demo nodes");
        Console.WriteLine("  --write-node <ns=2;s=Demo.Node>         Write target node");
        Console.WriteLine("  --write-value <value>                   Write value (bool/double/int/string)");
        Environment.Exit(0);
    }
}

internal static class BuildMode
{
#if DEBUG
    public const bool IsDebug = true;
#else
    public const bool IsDebug = false;
#endif
}
