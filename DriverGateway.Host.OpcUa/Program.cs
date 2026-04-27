using DriverGateway.Host.OpcUa.Config;
using DriverGateway.Host.OpcUa.Runtime;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using DriverGateway.Host.OpcUa;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var options = GatewayHostOptions.Parse(args);
        var autoAcceptUntrusted = BuildMode.IsDebug;

        var resolvedConfigPath = ResolvePath(options.ConfigPath);
        var resolvedPluginsPath = ResolvePath(options.PluginsPath);

        if (!GatewayConfigurationLoader.TryLoad(
                resolvedConfigPath,
                out var drivers,
                out var tagsByNodeId,
                out var configError))
        {
            throw new InvalidOperationException(configError);
        }

        var plugins = DriverPluginLoader.LoadPlugins(
            resolvedPluginsPath,
            message => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}"));

        var runtime = DriverGatewayRuntime.Build(
            drivers,
            tagsByNodeId,
            plugins,
            message => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}"));

        var applicationConfiguration = await CreateServerConfigurationAsync(options, autoAcceptUntrusted);
        var application = new ApplicationInstance
        {
            ApplicationName = options.Name,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = applicationConfiguration
        };

        var hasCertificate = await application.CheckApplicationInstanceCertificate(false, 2048);
        if (!hasCertificate)
        {
            throw new InvalidOperationException("Server certificate could not be created or loaded.");
        }

        runtime.Start();
        await application.Start(new GatewayOpcUaServer(runtime));

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Driver gateway host started.");
        Console.WriteLine($"Endpoint: {options.Endpoint}");
        Console.WriteLine($"Config: {resolvedConfigPath}");
        Console.WriteLine($"Plugins: {resolvedPluginsPath}");
        Console.WriteLine($"Tags loaded: {tagsByNodeId.Count}");
        Console.WriteLine($"Drivers loaded: {drivers.Count}");
        Console.WriteLine("Demand policy: subscription + one-shot read");
        Console.WriteLine("One-shot read: cache-first + async refresh");
        Console.WriteLine("Retry policy: exponential backoff + jitter");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var waitHandle = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            waitHandle.Set();
        };

        waitHandle.Wait();
        await runtime.DisposeAsync();
        application.Stop();
    }

    private static async Task<ApplicationConfiguration> CreateServerConfigurationAsync(
        GatewayHostOptions options,
        bool autoAcceptUntrusted)
    {
        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki", "driver_gateway");
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = options.Name,
            ApplicationUri = $"urn:{Utils.GetHostName()}:{options.Name}",
            ProductUri = "urn:opcua:drivergateway:host",
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
                    }
                }
            },
            TraceConfiguration = new TraceConfiguration()
        };

        await configuration.Validate(ApplicationType.Server);

        configuration.CertificateValidator.CertificateValidation += (_, eventArgs) =>
        {
            if (eventArgs.Error.StatusCode == StatusCodes.BadCertificateUntrusted && autoAcceptUntrusted)
            {
                eventArgs.Accept = true;
                Console.WriteLine($"[CERT] Auto-accepted untrusted certificate: {eventArgs.Certificate.Subject}");
            }
        };

        return configuration;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}

internal sealed class GatewayHostOptions
{
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840/UA/DriverGateway";
    public string Name { get; set; } = "DriverGatewayHostOpcUa";
    public string ConfigPath { get; set; } = "gateway.config.json";
    public string PluginsPath { get; set; } = "plugins";

    public static GatewayHostOptions Parse(string[] args)
    {
        var options = new GatewayHostOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--endpoint" when index + 1 < args.Length:
                    options.Endpoint = args[++index];
                    break;
                case "--name" when index + 1 < args.Length:
                    options.Name = args[++index];
                    break;
                case "--config" when index + 1 < args.Length:
                    options.ConfigPath = args[++index];
                    break;
                case "--plugins" when index + 1 < args.Length:
                    options.PluginsPath = args[++index];
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
        Console.WriteLine("DriverGateway.Host.OpcUa options:");
        Console.WriteLine("  --endpoint <opc.tcp://host:port/path>   Optional endpoint URL");
        Console.WriteLine("  --name <ApplicationName>                Optional server application name");
        Console.WriteLine("  --config <path-to-gateway.config.json>  Optional gateway config path");
        Console.WriteLine("  --plugins <plugins-folder>              Optional plugin folder path");
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

internal sealed class GatewayOpcUaServer : StandardServer
{
    private readonly DriverGatewayRuntime _runtime;

    public GatewayOpcUaServer(DriverGatewayRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        IList<INodeManager> nodeManagers = new List<INodeManager>
        {
            new GatewayNodeManager(server, configuration, _runtime)
        };

        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }
}
