using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

internal sealed class DynamicNodeManager : CustomNodeManager2
{
    private const string NamespaceUri = "urn:opcua:sample:dynamic";

    private readonly string _configPath;
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, DynamicTagRuntime> _tagRuntimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderState> _channelFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderState> _deviceFolders = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounceTimer;
    private FolderState? _dynamicFolder;
    private ushort _namespaceIndex;

    public DynamicNodeManager(IServerInternal server, ApplicationConfiguration configuration, string configPath)
        : base(server, configuration, NamespaceUri)
    {
        _configPath = configPath;
        SystemContext.NodeIdFactory = this;
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        return [];
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        _namespaceIndex = Server.NamespaceUris.GetIndexOrAppend(NamespaceUris.First());

        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
        {
            references = new List<IReference>();
            externalReferences[ObjectIds.ObjectsFolder] = references;
        }

        _dynamicFolder = CreateFolder(null, "Dynamic", new NodeId("Dynamic", _namespaceIndex));
        _dynamicFolder.AddReference(ReferenceTypeIds.Organizes, false, ObjectIds.ObjectsFolder);
        references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, true, _dynamicFolder.NodeId));

        AddPredefinedNode(SystemContext, _dynamicFolder);
        AddRootNotifier(_dynamicFolder);

        ApplyConfigurationFromDisk(initialLoad: true);
        StartConfigWatcher();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _watcher = null;

            _reloadDebounceTimer?.Dispose();
            _reloadDebounceTimer = null;

            foreach (var runtime in _tagRuntimes.Values)
            {
                runtime.Dispose();
            }

            _tagRuntimes.Clear();
            _channelFolders.Clear();
            _deviceFolders.Clear();
        }

        base.Dispose(disposing);
    }

    private void ApplyConfigurationFromDisk(bool initialLoad)
    {
        if (!NodeConfigurationLoader.TryLoad(_configPath, out var configuration, out var error))
        {
            Console.WriteLine($"[CONFIG] {error}");
            if (initialLoad)
            {
                Console.WriteLine("[CONFIG] No valid configuration loaded at startup. Waiting for file changes.");
            }

            return;
        }

        var desiredTags = NodeConfigurationCompiler.Compile(configuration);

        lock (_syncRoot)
        {
            ReconcileTags(desiredTags);
        }

        Console.WriteLine($"[CONFIG] Applied {desiredTags.Count} tags from {_configPath}");
    }

    private void ReconcileTags(IReadOnlyDictionary<string, TagDescriptor> desiredTags)
    {
        var existingKeys = _tagRuntimes.Keys.ToArray();
        foreach (var existingKey in existingKeys)
        {
            if (!desiredTags.ContainsKey(existingKey))
            {
                RemoveTagRuntime(existingKey);
            }
        }

        foreach (var (key, descriptor) in desiredTags)
        {
            if (_tagRuntimes.TryGetValue(key, out var currentRuntime))
            {
                if (currentRuntime.Descriptor == descriptor)
                {
                    continue;
                }

                RemoveTagRuntime(key);
            }

            AddTagRuntime(descriptor);
        }
    }

    private void AddTagRuntime(TagDescriptor descriptor)
    {
        if (_dynamicFolder is null)
        {
            return;
        }

        ITagValueProvider provider;
        try
        {
            provider = TagValueProviderFactory.Create(descriptor.Provider);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] Skipping {descriptor.NodeIdentifier}. {ex.Message}");
            return;
        }

        var deviceFolder = GetOrCreateDeviceFolder(descriptor.ChannelName, descriptor.DeviceName);

        if (!TryConvertValue(
                provider.GetNextValue(DateTime.UtcNow),
                descriptor.DataType,
                out var initialValue,
                out var conversionError))
        {
            Console.WriteLine($"[CONFIG] Skipping {descriptor.NodeIdentifier}. {conversionError}");
            return;
        }

        var variable = new BaseDataVariableState(deviceFolder)
        {
            SymbolicName = descriptor.TagName,
            NodeId = new NodeId(descriptor.NodeIdentifier, _namespaceIndex),
            BrowseName = new QualifiedName(descriptor.TagName, _namespaceIndex),
            DisplayName = new LocalizedText("en", descriptor.TagName),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = GetOpcDataType(descriptor.DataType),
            ValueRank = ValueRanks.Scalar,
            AccessLevel = descriptor.IsWritable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            UserAccessLevel = descriptor.IsWritable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            Historizing = false,
            Value = initialValue,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
            OnSimpleWriteValue = descriptor.IsWritable ? HandleTagWrite : null
        };

        deviceFolder.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);

        var runtime = new DynamicTagRuntime(descriptor, variable, deviceFolder, provider);
        runtime.StartTimer(OnProviderTimerElapsed);
        _tagRuntimes[descriptor.NodeIdentifier] = runtime;

        Console.WriteLine($"[TAG] Added ns=2;s={descriptor.NodeIdentifier}");
    }

    private void RemoveTagRuntime(string key)
    {
        if (!_tagRuntimes.TryRemove(key, out var runtime))
        {
            return;
        }

        runtime.Dispose();
        runtime.ParentFolder.RemoveChild(runtime.Variable);
        TryRemovePredefinedNode(runtime.Variable);
        Console.WriteLine($"[TAG] Removed ns=2;s={key}");
    }

    private ServiceResult HandleTagWrite(ISystemContext context, NodeState node, ref object value)
    {
        lock (_syncRoot)
        {
            if (node is not BaseDataVariableState variable || variable.NodeId?.Identifier is not string nodeIdentifier)
            {
                return StatusCodes.BadNodeIdUnknown;
            }

            if (!_tagRuntimes.TryGetValue(nodeIdentifier, out var runtime))
            {
                return StatusCodes.BadNodeIdUnknown;
            }

            if (!runtime.Descriptor.IsWritable)
            {
                return StatusCodes.BadNotWritable;
            }

            if (!TryConvertValue(value, runtime.Descriptor.DataType, out var convertedValue, out var conversionError))
            {
                Console.WriteLine($"[WRITE] Conversion error for {runtime.Descriptor.NodeIdentifier}: {conversionError}");
                return StatusCodes.BadTypeMismatch;
            }

            value = convertedValue;
            ApplyValue(runtime.Variable, convertedValue, context);

            if (runtime.Descriptor.WritePolicyTtlSeconds > 0)
            {
                runtime.WriteOverrideUntilUtc = DateTime.UtcNow.AddSeconds(runtime.Descriptor.WritePolicyTtlSeconds);
            }
            else
            {
                runtime.WriteOverrideUntilUtc = null;
            }
        }

        return ServiceResult.Good;
    }

    private void OnProviderTimerElapsed(object? state)
    {
        if (state is not DynamicTagRuntime runtime)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_tagRuntimes.TryGetValue(runtime.Descriptor.NodeIdentifier, out var activeRuntime) ||
                !ReferenceEquals(activeRuntime, runtime))
            {
                return;
            }

            if (runtime.WriteOverrideUntilUtc.HasValue && runtime.WriteOverrideUntilUtc.Value > DateTime.UtcNow)
            {
                return;
            }

            var rawValue = runtime.Provider.GetNextValue(DateTime.UtcNow);
            if (!TryConvertValue(rawValue, runtime.Descriptor.DataType, out var convertedValue, out var conversionError))
            {
                Console.WriteLine($"[TAG] Conversion error for {runtime.Descriptor.NodeIdentifier}: {conversionError}");
                return;
            }

            ApplyValue(runtime.Variable, convertedValue, SystemContext);
        }
    }

    private void StartConfigWatcher()
    {
        var directory = Path.GetDirectoryName(_configPath);
        var fileName = Path.GetFileName(_configPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _reloadDebounceTimer = new Timer(_ => ApplyConfigurationFromDisk(initialLoad: false));

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Deleted += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    private void ScheduleReload()
    {
        _reloadDebounceTimer?.Change(500, Timeout.Infinite);
    }

    private void ApplyValue(BaseDataVariableState variable, object value, ISystemContext context)
    {
        variable.Value = value;
        variable.Timestamp = DateTime.UtcNow;
        variable.StatusCode = StatusCodes.Good;
        variable.ClearChangeMasks(context, false);
    }

    private FolderState GetOrCreateDeviceFolder(string channelName, string deviceName)
    {
        var channelFolder = GetOrCreateChannelFolder(channelName);
        var deviceKey = $"{channelName}/{deviceName}";

        if (_deviceFolders.TryGetValue(deviceKey, out var existingDeviceFolder))
        {
            return existingDeviceFolder;
        }

        var deviceFolder = CreateFolder(
            channelFolder,
            deviceName,
            new NodeId($"Dynamic.{channelName}.{deviceName}", _namespaceIndex));

        channelFolder.AddChild(deviceFolder);
        AddPredefinedNode(SystemContext, deviceFolder);
        _deviceFolders[deviceKey] = deviceFolder;
        return deviceFolder;
    }

    private FolderState GetOrCreateChannelFolder(string channelName)
    {
        if (_dynamicFolder is null)
        {
            throw new InvalidOperationException("Dynamic root folder was not initialized.");
        }

        if (_channelFolders.TryGetValue(channelName, out var existingChannelFolder))
        {
            return existingChannelFolder;
        }

        var channelFolder = CreateFolder(
            _dynamicFolder,
            channelName,
            new NodeId($"Dynamic.{channelName}", _namespaceIndex));

        _dynamicFolder.AddChild(channelFolder);
        AddPredefinedNode(SystemContext, channelFolder);
        _channelFolders[channelName] = channelFolder;
        return channelFolder;
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

        return folder;
    }

    private void TryRemovePredefinedNode(NodeState node)
    {
        try
        {
            var removeMethod = typeof(CustomNodeManager2).GetMethod(
                "RemovePredefinedNode",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (removeMethod is null)
            {
                return;
            }

            var parameters = removeMethod.GetParameters();
            switch (parameters.Length)
            {
                case 1:
                    removeMethod.Invoke(this, [node]);
                    break;
                case 2:
                    removeMethod.Invoke(this, [SystemContext, node]);
                    break;
            }
        }
        catch
        {
            // Best-effort cleanup. Parent-child unlink already handled.
        }
    }

    private static NodeId GetOpcDataType(TagDataType dataType)
    {
        return dataType switch
        {
            TagDataType.Double => DataTypeIds.Double,
            TagDataType.Float => DataTypeIds.Float,
            TagDataType.Boolean => DataTypeIds.Boolean,
            TagDataType.Int16 => DataTypeIds.Int16,
            TagDataType.UInt16 => DataTypeIds.UInt16,
            TagDataType.Word => DataTypeIds.UInt16,
            TagDataType.Int32 => DataTypeIds.Int32,
            TagDataType.UInt32 => DataTypeIds.UInt32,
            TagDataType.DWord => DataTypeIds.UInt32,
            TagDataType.String => DataTypeIds.String,
            _ => DataTypeIds.Double
        };
    }

    private static bool TryConvertValue(object? rawValue, TagDataType dataType, out object value, out string error)
    {
        value = 0d;
        error = string.Empty;

        try
        {
            switch (dataType)
            {
                case TagDataType.Double:
                    value = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                    return true;

                case TagDataType.Float:
                    value = Convert.ToSingle(rawValue, CultureInfo.InvariantCulture);
                    return true;

                case TagDataType.Boolean:
                    value = rawValue switch
                    {
                        bool boolValue => boolValue,
                        string stringValue when bool.TryParse(stringValue, out var parsedBool) => parsedBool,
                        _ => Math.Abs(Convert.ToDouble(rawValue, CultureInfo.InvariantCulture)) > double.Epsilon
                    };
                    return true;

                case TagDataType.Int32:
                    value = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
                    return true;

                case TagDataType.Int16:
                    value = Convert.ToInt16(rawValue, CultureInfo.InvariantCulture);
                    return true;

                case TagDataType.UInt16:
                case TagDataType.Word:
                    value = Convert.ToUInt16(rawValue, CultureInfo.InvariantCulture);
                    return true;

                case TagDataType.UInt32:
                case TagDataType.DWord:
                    value = Convert.ToUInt32(rawValue, CultureInfo.InvariantCulture);
                    return true;

                case TagDataType.String:
                    value = rawValue?.ToString() ?? string.Empty;
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        error = $"Unsupported tag data type: {dataType}";
        return false;
    }
}

internal sealed class DynamicTagRuntime : IDisposable
{
    private Timer? _updateTimer;

    public DynamicTagRuntime(
        TagDescriptor descriptor,
        BaseDataVariableState variable,
        FolderState parentFolder,
        ITagValueProvider provider)
    {
        Descriptor = descriptor;
        Variable = variable;
        ParentFolder = parentFolder;
        Provider = provider;
    }

    public TagDescriptor Descriptor { get; }
    public BaseDataVariableState Variable { get; }
    public FolderState ParentFolder { get; }
    public ITagValueProvider Provider { get; }
    public DateTime? WriteOverrideUntilUtc { get; set; }

    public void StartTimer(TimerCallback callback)
    {
        _updateTimer = new Timer(
            callback,
            this,
            dueTime: Descriptor.Provider.IntervalMs,
            period: Descriptor.Provider.IntervalMs);
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
        _updateTimer = null;
    }
}
