using System.Globalization;
using System.Text.Json;
using DriverGateway.Client.OpcUa.Config;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

internal static class Program
{
    private static readonly object ReconnectSyncRoot = new();

    private static Session? _session;
    private static SessionReconnectHandler? _reconnectHandler;
    private static int _reconnectPeriodMs = 5_000;

    public static async Task Main(string[] args)
    {
        var options = ClientOptions.Parse(args);
        var autoAcceptUntrusted = BuildMode.IsDebug;
        var resolvedConfigPath = ResolvePath(options.ConfigPath);

        if (!ClientConfigLoader.TryLoad(resolvedConfigPath, out var clientConfig, out var configError))
        {
            throw new InvalidOperationException(configError);
        }

        _reconnectPeriodMs = clientConfig.Session.ReconnectPeriodMs <= 0 ? 5_000 : clientConfig.Session.ReconnectPeriodMs;

        var appConfig = await CreateClientConfigurationAsync(autoAcceptUntrusted);
        var application = new ApplicationInstance
        {
            ApplicationName = "DriverGatewayClientOpcUa",
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = appConfig
        };

        var hasCertificate = await application.CheckApplicationInstanceCertificate(false, 2048);
        if (!hasCertificate)
        {
            throw new InvalidOperationException("Client application certificate could not be created or loaded.");
        }

        var endpointDescription = CoreClientUtils.SelectEndpoint(clientConfig.Endpoint, false, 15_000);
        EnsureEndpointSecurityNone(endpointDescription);

        var endpointConfiguration = EndpointConfiguration.Create(appConfig);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration)
        {
            UpdateBeforeConnect = true
        };

        var sessionTimeout = clientConfig.Session.SessionTimeoutMs <= 0
            ? 60_000u
            : (uint)clientConfig.Session.SessionTimeoutMs;

        _session = await Session.Create(
            appConfig,
            configuredEndpoint,
            updateBeforeConnect: false,
            sessionName: "DriverGatewayClientSession",
            sessionTimeout: sessionTimeout,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null);

        _session.KeepAlive += SessionOnKeepAlive;

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Connected to {_session.Endpoint.EndpointUrl}");
        Console.WriteLine("Security: None");
        Console.WriteLine($"Config: {resolvedConfigPath}");
        Console.WriteLine($"Trust Mode: {(autoAcceptUntrusted ? "Debug auto-trust" : "Release manual trust")}");

        RunOneShotRead(_session, clientConfig.ReadNodes);
        
        if (options.ApplyWrites)
        {
            ApplyWritesFromConfig(_session, clientConfig.Writes);
        }
        else if (clientConfig.Writes.Count > 0)
        {
            Console.WriteLine("Write list found in config but skipped (use --apply-writes to execute).");
        }

        if (clientConfig.SubscribeNodes.Count > 0)
        {
            using var subscription = CreateSubscription(
                _session,
                clientConfig.SubscribeNodes,
                clientConfig.Session.PublishingIntervalMs,
                clientConfig.Session.SamplingIntervalMs);

            Console.WriteLine("Subscription active. Press Ctrl+C to exit.");

            using var waitHandle = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                waitHandle.Set();
            };

            waitHandle.Wait();
        }
        else
        {
            Console.WriteLine("No subscribeNodes configured. Client will exit after startup reads.");
        }

        CloseSession();
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Client stopped.");
    }

    private static async Task<ApplicationConfiguration> CreateClientConfigurationAsync(bool autoAcceptUntrusted)
    {
        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki", "client_driver_gateway");

        var config = new ApplicationConfiguration
        {
            ApplicationName = "DriverGatewayClientOpcUa",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{Utils.GetHostName()}:DriverGatewayClientOpcUa",
            ProductUri = "urn:opcua:drivergateway:client",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=DriverGatewayClientOpcUa, O=OPC_UA"
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
                OperationTimeout = 15_000,
                MaxStringLength = 1_048_576,
                MaxByteStringLength = 1_048_576,
                MaxMessageSize = 4_194_304,
                MaxBufferSize = 65_535,
                ChannelLifetime = 300_000,
                SecurityTokenLifetime = 3_600_000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60_000,
                MinSubscriptionLifetime = 10_000
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

    private static void EnsureEndpointSecurityNone(EndpointDescription endpoint)
    {
        if (endpoint.SecurityMode != MessageSecurityMode.None || endpoint.SecurityPolicyUri != SecurityPolicies.None)
        {
            throw new InvalidOperationException(
                $"Selected endpoint is not Security None. Mode={endpoint.SecurityMode}, Policy={endpoint.SecurityPolicyUri}");
        }
    }

    private static void RunOneShotRead(Session session, IReadOnlyList<string> readNodes)
    {
        if (readNodes.Count == 0)
        {
            Console.WriteLine("No readNodes configured.");
            return;
        }

        var parsedNodes = ParseConfiguredNodes(readNodes);
        var nodesToRead = new ReadValueIdCollection(
            parsedNodes.Select(static node => new ReadValueId
            {
                NodeId = node.NodeId,
                AttributeId = Attributes.Value
            }));

        session.Read(
            null,
            maxAge: 0,
            timestampsToReturn: TimestampsToReturn.Both,
            nodesToRead,
            out var results,
            out var diagnosticInfos);

        ClientBase.ValidateResponse(results, nodesToRead);
        ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToRead);

        Console.WriteLine("One-shot read:");
        for (var index = 0; index < parsedNodes.Count; index++)
        {
            Console.WriteLine($"  {parsedNodes[index].Raw} => {results[index].Value}");
        }
    }

    private static void ApplyWritesFromConfig(Session session, IReadOnlyList<WriteItemConfig> writes)
    {
        if (writes.Count == 0)
        {
            Console.WriteLine("No writes configured.");
            return;
        }

        var writeValues = new WriteValueCollection();
        var display = new List<string>(writes.Count);

        foreach (var write in writes)
        {
            if (!NodeIdParser.TryParse(write.NodeId, out var nodeId))
            {
                throw new InvalidOperationException($"Invalid write node id: {write.NodeId}");
            }

            var value = ParseConfiguredValue(write.Type, write.Value, write.NodeId);
            writeValues.Add(new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            });
            display.Add($"{write.NodeId} <= {value}");
        }

        session.Write(null, writeValues, out var results, out var diagnosticInfos);
        ClientBase.ValidateResponse(results, writeValues);
        ClientBase.ValidateDiagnosticInfos(diagnosticInfos, writeValues);

        Console.WriteLine("Config writes applied:");
        foreach (var row in display)
        {
            Console.WriteLine($"  {row}");
        }
    }

    private static Subscription CreateSubscription(
        Session session,
        IReadOnlyList<string> subscribeNodes,
        int publishingIntervalMs,
        int samplingIntervalMs)
    {
        var parsedNodes = ParseConfiguredNodes(subscribeNodes);

        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = "DriverGatewayClientSubscription",
            PublishingInterval = publishingIntervalMs <= 0 ? 1_000 : publishingIntervalMs,
            KeepAliveCount = 10,
            LifetimeCount = 60,
            MaxNotificationsPerPublish = 100,
            PublishingEnabled = true,
            Priority = 100
        };

        foreach (var node in parsedNodes)
        {
            var monitoredItem = new MonitoredItem
            {
                DisplayName = node.Raw,
                StartNodeId = node.NodeId,
                AttributeId = Attributes.Value,
                MonitoringMode = MonitoringMode.Reporting,
                SamplingInterval = samplingIntervalMs <= 0 ? 1_000 : samplingIntervalMs,
                QueueSize = 20,
                DiscardOldest = true
            };

            monitoredItem.Notification += (_, _) =>
            {
                foreach (var value in monitoredItem.DequeueValues())
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {node.Raw} => {value.Value}");
                }
            };

            subscription.AddItem(monitoredItem);
        }

        session.AddSubscription(subscription);
        subscription.Create();
        return subscription;
    }

    private static List<ParsedNode> ParseConfiguredNodes(IEnumerable<string> rawNodes)
    {
        var parsed = new List<ParsedNode>();
        foreach (var raw in rawNodes)
        {
            if (!NodeIdParser.TryParse(raw, out var nodeId))
            {
                throw new InvalidOperationException($"Configured node id is invalid: {raw}");
            }

            parsed.Add(new ParsedNode(raw, nodeId));
        }

        return parsed;
    }

    private static object ParseConfiguredValue(string type, JsonElement element, string nodeId)
    {
        var normalized = type.Trim().ToLowerInvariant();

        try
        {
            return normalized switch
            {
                "bool" or "boolean" => element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.Parse(element.GetString() ?? "false"),
                    JsonValueKind.Number => Math.Abs(element.GetDouble()) > double.Epsilon,
                    _ => throw new InvalidOperationException()
                },
                "int" or "int32" => element.ValueKind switch
                {
                    JsonValueKind.Number => element.GetInt32(),
                    JsonValueKind.String => int.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException()
                },
                "double" => element.ValueKind switch
                {
                    JsonValueKind.Number => element.GetDouble(),
                    JsonValueKind.String => double.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException()
                },
                "float" => element.ValueKind switch
                {
                    JsonValueKind.Number => element.GetSingle(),
                    JsonValueKind.String => float.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException()
                },
                "string" => element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    _ => element.GetRawText()
                },
                _ => throw new InvalidOperationException($"Unsupported write type '{type}'.")
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse write value for node '{nodeId}' with type '{type}': {ex.Message}");
        }
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
            _reconnectHandler.BeginReconnect(session, _reconnectPeriodMs, ClientReconnectComplete);
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

    private static string ResolvePath(string rawPath)
    {
        if (Path.IsPathRooted(rawPath))
        {
            return rawPath;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rawPath));
    }
}

internal sealed record ParsedNode(string Raw, NodeId NodeId);

internal sealed class ClientOptions
{
    public string ConfigPath { get; set; } = "client.config.json";
    //public string ConfigPath { get; set; } = "client.write.json";

    public bool ApplyWrites { get; set; }

    public static ClientOptions Parse(string[] args)
    {
        var options = new ClientOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--config" when index + 1 < args.Length:
                    options.ConfigPath = args[++index];
                    break;
                case "--apply-writes":
                    options.ApplyWrites = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {current}");
            }
        }

        return options;
    }

    private static void PrintHelpAndExit()
    {
        Console.WriteLine("DriverGateway.Client.OpcUa options:");
        Console.WriteLine("  --config <path-to-client.config.json>   Optional config file path");
        Console.WriteLine("  --apply-writes                          Execute writes[] from config");
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
