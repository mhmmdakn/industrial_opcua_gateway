using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var options = ServerDynamicOptions.Parse(args);
        var autoAcceptUntrusted = BuildMode.IsDebug;
        var resolvedConfigPath = ResolveConfigPath(options.ConfigPath);

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

        await application.Start(new DynamicSampleServer(resolvedConfigPath));

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Dynamic server started.");
        Console.WriteLine($"Endpoint: {options.Endpoint}");
        Console.WriteLine($"Config file: {resolvedConfigPath}");
        Console.WriteLine("Model: Channel > Device > Tag");
        Console.WriteLine("NodeId format: ns=2;s={Channel}.{Device}.{Tag}");
        Console.WriteLine("Security options:");
        Console.WriteLine("  None + None");
        Console.WriteLine("  Basic256 + Sign");
        Console.WriteLine("  Basic256 + SignAndEncrypt");
        Console.WriteLine("  Basic256Sha256 + Sign");
        Console.WriteLine("  Basic256Sha256 + SignAndEncrypt");
        Console.WriteLine("User tokens:");
        Console.WriteLine("  Anonymous");
        Console.WriteLine("  UserName (Basic256Sha256)");
        Console.WriteLine("  X509 Certificate (Basic256Sha256)");
        Console.WriteLine($"Trust Mode: {(autoAcceptUntrusted ? "Debug auto-trust" : "Release manual trust")}");
        Console.WriteLine("Hot reload: Add + Update + Remove");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var waitHandle = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            waitHandle.Set();
        };

        waitHandle.Wait();
        application.Stop();

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Dynamic server stopped.");
    }

    private static async Task<ApplicationConfiguration> CreateServerConfigurationAsync(
        ServerDynamicOptions options,
        bool autoAcceptUntrusted)
    {
        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki", "server_dynamic");

        var config = new ApplicationConfiguration
        {
            ApplicationName = options.Name,
            ApplicationUri = $"urn:{Utils.GetHostName()}:{options.Name}",
            ProductUri = "urn:opcua:sample:dynamicserver",
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

    private static string ResolveConfigPath(string configPath)
    {
        if (Path.IsPathRooted(configPath))
        {
            return configPath;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configPath));
    }
}

internal sealed record ServerDynamicOptions(string Endpoint, string Name, string ConfigPath)
{
    public static ServerDynamicOptions Parse(string[] args)
    {
        var endpoint = "opc.tcp://localhost:4840/UA/SampleServer";
        var name = "OpcUaServerDynamicApp";
        var configPath = "nodes.json";

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
                case "--config" when index + 1 < args.Length:
                    configPath = args[++index];
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {current}");
            }
        }

        return new ServerDynamicOptions(endpoint, name, configPath);
    }

    private static void PrintHelpAndExit()
    {
        Console.WriteLine("OpcUaServerDynamicApp options:");
        Console.WriteLine("  --endpoint <opc.tcp://host:port/path>   Optional endpoint URL");
        Console.WriteLine("  --name <ApplicationName>                Optional server application name");
        Console.WriteLine("  --config <path-to-nodes.json>           Optional node configuration path");
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

internal sealed class DynamicSampleServer : StandardServer
{
    private readonly string _configPath;

    public DynamicSampleServer(string configPath)
    {
        _configPath = configPath;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        IList<INodeManager> nodeManagers = new List<INodeManager>
        {
            new DynamicNodeManager(server, configuration, _configPath)
        };

        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }
}
