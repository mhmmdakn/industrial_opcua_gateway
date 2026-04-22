using System.Globalization;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var options = ServerOptions.Parse(args);
        var autoAcceptUntrusted = BuildMode.IsDebug;

        var configuration = await CreateServerConfigurationAsync(options, autoAcceptUntrusted);
        var application = new ApplicationInstance
        {
            ApplicationName = options.Name,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };

        var hasCertificate = await application.CheckApplicationInstanceCertificate(false, 2048);
        if (!hasCertificate)
        {
            throw new InvalidOperationException("Server application certificate could not be created or loaded.");
        }

        await application.Start(new SampleServer());

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Server started.");
        Console.WriteLine($"Endpoint: {options.Endpoint}");
        Console.WriteLine("Security options:");
        Console.WriteLine("  None + None");
        Console.WriteLine("  Basic256 + Sign");
        Console.WriteLine("  Basic256 + SignAndEncrypt");
        Console.WriteLine("  Basic256Sha256 + Sign");
        Console.WriteLine("  Basic256Sha256 + SignAndEncrypt");
        Console.WriteLine($"Trust Mode: {(autoAcceptUntrusted ? "Debug auto-trust" : "Release manual trust")}");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var waitHandle = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            waitHandle.Set();
        };

        waitHandle.Wait();
        application.Stop();

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Server stopped.");
    }

    private static async Task<ApplicationConfiguration> CreateServerConfigurationAsync(ServerOptions options, bool autoAcceptUntrusted)
    {
        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki", "server");

        var config = new ApplicationConfiguration
        {
            ApplicationName = options.Name,
            ApplicationUri = $"urn:{Utils.GetHostName()}:{options.Name}",
            ProductUri = "urn:opcua:sample:server",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN={options.Name}, O=OPC_UA"
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
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { options.Endpoint },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new()
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    },
                    new()
                    {
                        SecurityMode = MessageSecurityMode.Sign,
                        SecurityPolicyUri = SecurityPolicies.Basic256
                    },
                    new()
                    {
                        SecurityMode = MessageSecurityMode.SignAndEncrypt,
                        SecurityPolicyUri = SecurityPolicies.Basic256
                    },
                    new()
                    {
                        SecurityMode = MessageSecurityMode.Sign,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                    },
                    new()
                    {
                        SecurityMode = MessageSecurityMode.SignAndEncrypt,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                    }
                },
                UserTokenPolicies = new UserTokenPolicyCollection
                {
                    new()
                    {
                        PolicyId = "anonymous",
                        TokenType = UserTokenType.Anonymous
                    },
                    new()
                    {
                        PolicyId = "username_basic256sha256",
                        TokenType = UserTokenType.UserName,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                    },
                    new()
                    {
                        PolicyId = "x509_basic256sha256",
                        TokenType = UserTokenType.Certificate,
                        SecurityPolicyUri = SecurityPolicies.Basic256Sha256
                    }
                },
                DiagnosticsEnabled = true
            },
            TraceConfiguration = new TraceConfiguration()
        };

        await config.Validate(ApplicationType.Server);

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
}

internal sealed record ServerOptions(string Endpoint, string Name)
{
    public static ServerOptions Parse(string[] args)
    {
        var endpoint = "opc.tcp://localhost:4840/UA/SampleServer";
        var name = "OpcUaSampleServer";

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--endpoint" when index + 1 < args.Length:
                    endpoint = args[++index];
                    break;
                case "--name" when index + 1 < args.Length:
                    name = args[++index];
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {current}");
            }
        }

        return new ServerOptions(endpoint, name);
    }

    private static void PrintHelpAndExit()
    {
        Console.WriteLine("OpcUaServerApp options:");
        Console.WriteLine("  --endpoint <opc.tcp://host:port/path>   Optional endpoint URL");
        Console.WriteLine("  --name <ApplicationName>                Optional server application name");
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

internal sealed class SampleServer : StandardServer
{
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        IList<INodeManager> nodeManagers = new List<INodeManager>
        {
            new DemoNodeManager(server, configuration)
        };

        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }
}

internal sealed class DemoNodeManager : CustomNodeManager2
{
    private const string NamespaceUri = "urn:opcua:sample:demo";

    private readonly object _syncRoot = new();
    private readonly Random _random = new();

    private ushort _namespaceIndex;
    private Timer? _simulationTimer;

    private BaseDataVariableState? _temperature;
    private BaseDataVariableState? _pressure;
    private BaseDataVariableState? _machineRunning;

    public DemoNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, NamespaceUri)
    {
        SystemContext.NodeIdFactory = this;
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        return new NodeStateCollection();
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        _namespaceIndex = Server.NamespaceUris.GetIndexOrAppend(NamespaceUris.First());

        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
        {
            references = new List<IReference>();
            externalReferences[ObjectIds.ObjectsFolder] = references;
        }

        var demoFolder = CreateFolder(null, "Demo", new NodeId("Demo", _namespaceIndex));
        demoFolder.AddReference(ReferenceTypeIds.Organizes, false, ObjectIds.ObjectsFolder);
        references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, true, demoFolder.NodeId));

        AddPredefinedNode(SystemContext, demoFolder);
        AddRootNotifier(demoFolder);

        _temperature = CreateVariable(demoFolder, "Temperature", "Demo.Temperature", DataTypeIds.Double, 24.5d);
        _pressure = CreateVariable(demoFolder, "Pressure", "Demo.Pressure", DataTypeIds.Double, 1.25d);
        _machineRunning = CreateVariable(demoFolder, "MachineRunning", "Demo.MachineRunning", DataTypeIds.Boolean, true);

        _simulationTimer = new Timer(UpdateSimulation, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Console.WriteLine("Demo nodes published:");
        Console.WriteLine("  ns=2;s=Demo.Temperature");
        Console.WriteLine("  ns=2;s=Demo.Pressure");
        Console.WriteLine("  ns=2;s=Demo.MachineRunning");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _simulationTimer?.Dispose();
            _simulationTimer = null;
        }

        base.Dispose(disposing);
    }

    private FolderState CreateFolder(NodeState? parent, string name, NodeId nodeId)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            NodeId = nodeId,
            BrowseName = new QualifiedName(name, _namespaceIndex),
            DisplayName = new LocalizedText("en", name),
            TypeDefinitionId = ObjectTypeIds.FolderType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };

        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(NodeState parent, string browseName, string stringNodeId, NodeId dataType, object initialValue)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = browseName,
            NodeId = new NodeId(stringNodeId, _namespaceIndex),
            BrowseName = new QualifiedName(browseName, _namespaceIndex),
            DisplayName = new LocalizedText("en", browseName),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Historizing = false,
            Value = initialValue,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
            OnSimpleWriteValue = OnWriteValue
        };

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);

        return variable;
    }

    private ServiceResult OnWriteValue(ISystemContext context, NodeState node, ref object value)
    {
        lock (_syncRoot)
        {
            if (node == _machineRunning)
            {
                value = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            else
            {
                value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            if (node is BaseDataVariableState variable)
            {
                variable.Value = value;
                variable.Timestamp = DateTime.UtcNow;
                variable.StatusCode = StatusCodes.Good;
                variable.ClearChangeMasks(context, false);
            }
        }

        return ServiceResult.Good;
    }

    private void UpdateSimulation(object? _)
    {
        lock (_syncRoot)
        {
            if (_temperature is null || _pressure is null || _machineRunning is null)
            {
                return;
            }

            var machineRunning = Convert.ToBoolean(_machineRunning.Value, CultureInfo.InvariantCulture);
            if (machineRunning)
            {
                var nextTemp = Convert.ToDouble(_temperature.Value, CultureInfo.InvariantCulture) + ((_random.NextDouble() - 0.5d) * 0.8d);
                var nextPressure = Convert.ToDouble(_pressure.Value, CultureInfo.InvariantCulture) + ((_random.NextDouble() - 0.5d) * 0.1d);

                _temperature.Value = Math.Round(nextTemp, 2);
                _pressure.Value = Math.Round(Math.Max(0.1d, nextPressure), 2);
            }

            var now = DateTime.UtcNow;

            _temperature.Timestamp = now;
            _temperature.StatusCode = StatusCodes.Good;
            _temperature.ClearChangeMasks(SystemContext, false);

            _pressure.Timestamp = now;
            _pressure.StatusCode = StatusCodes.Good;
            _pressure.ClearChangeMasks(SystemContext, false);

            _machineRunning.Timestamp = now;
            _machineRunning.StatusCode = StatusCodes.Good;
            _machineRunning.ClearChangeMasks(SystemContext, false);
        }
    }
}
