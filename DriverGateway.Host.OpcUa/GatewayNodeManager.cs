using DriverGateway.Core.Models;
using DriverGateway.Host.OpcUa.Runtime;
using Opc.Ua;
using Opc.Ua.Server;

namespace DriverGateway.Host.OpcUa;

internal sealed class GatewayNodeManager : CustomNodeManager2
{
    private const string NamespaceUri = "urn:opcua:drivergateway:v1";

    private readonly DriverGatewayRuntime _runtime;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, BaseDataVariableState> _variablesByNodeId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderState> _channelFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderState> _deviceFolders =
        new(StringComparer.OrdinalIgnoreCase);

    private ushort _namespaceIndex;
    private FolderState? _rootFolder;
    private Timer? _cacheSyncTimer;

    public GatewayNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        DriverGatewayRuntime runtime)
        : base(server, configuration, NamespaceUri)
    {
        _runtime = runtime;
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

        _rootFolder = CreateFolder(null, "DriverGateway", new NodeId("DriverGateway", _namespaceIndex));
        _rootFolder.AddReference(ReferenceTypeIds.Organizes, false, ObjectIds.ObjectsFolder);
        references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, true, _rootFolder.NodeId));

        AddPredefinedNode(SystemContext, _rootFolder);
        AddRootNotifier(_rootFolder);

        BuildTagNodes();
        _cacheSyncTimer = new Timer(_ => SyncFromCache(), null, dueTime: 200, period: 200);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cacheSyncTimer?.Dispose();
            _cacheSyncTimer = null;
            _variablesByNodeId.Clear();
            _channelFolders.Clear();
            _deviceFolders.Clear();
        }

        base.Dispose(disposing);
    }

    protected override void OnMonitoredItemCreated(
        ServerSystemContext context,
        NodeHandle handle,
        MonitoredItem monitoredItem)
    {
        base.OnMonitoredItemCreated(context, handle, monitoredItem);
        var nodeIdentifier = ExtractNodeIdentifier(handle, monitoredItem);
        if (!string.IsNullOrWhiteSpace(nodeIdentifier))
        {
            _runtime.AddSubscriptionDemand(nodeIdentifier);
        }
    }

    protected override void OnMonitoredItemDeleted(
        ServerSystemContext context,
        NodeHandle handle,
        MonitoredItem monitoredItem)
    {
        base.OnMonitoredItemDeleted(context, handle, monitoredItem);
        var nodeIdentifier = ExtractNodeIdentifier(handle, monitoredItem);
        if (!string.IsNullOrWhiteSpace(nodeIdentifier))
        {
            _runtime.RemoveSubscriptionDemand(nodeIdentifier);
        }
    }

    protected override void OnMonitoringModeChanged(
        ServerSystemContext context,
        NodeHandle handle,
        MonitoredItem monitoredItem,
        MonitoringMode previousMode,
        MonitoringMode currentMode)
    {
        base.OnMonitoringModeChanged(context, handle, monitoredItem, previousMode, currentMode);
        var nodeIdentifier = ExtractNodeIdentifier(handle, monitoredItem);
        if (string.IsNullOrWhiteSpace(nodeIdentifier))
        {
            return;
        }

        if (currentMode == MonitoringMode.Reporting && previousMode != MonitoringMode.Reporting)
        {
            _runtime.AddSubscriptionDemand(nodeIdentifier);
        }
        else if (previousMode == MonitoringMode.Reporting && currentMode != MonitoringMode.Reporting)
        {
            _runtime.RemoveSubscriptionDemand(nodeIdentifier);
        }
    }

    private void BuildTagNodes()
    {
        if (_rootFolder is null)
        {
            throw new InvalidOperationException("Root folder is not initialized.");
        }

        foreach (var tag in _runtime.TagsByNodeId.Values)
        {
            var deviceFolder = GetOrCreateDeviceFolder(tag.ChannelName, tag.DeviceName);
            var variable = new BaseDataVariableState(deviceFolder)
            {
                SymbolicName = tag.TagName,
                NodeId = new NodeId(tag.NodeIdentifier, _namespaceIndex),
                BrowseName = new QualifiedName(tag.TagName, _namespaceIndex),
                DisplayName = new LocalizedText("en", tag.TagName),
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                DataType = ResolveOpcDataType(tag.DataType),
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                Historizing = false,
                Value = GetDefaultValue(tag.DataType),
                StatusCode = StatusCodes.UncertainInitialValue,
                Timestamp = DateTime.UtcNow,
                OnSimpleReadValue = HandleTagRead,
                OnSimpleWriteValue = HandleTagWrite
            };

            deviceFolder.AddChild(variable);
            AddPredefinedNode(SystemContext, variable);
            _variablesByNodeId[tag.NodeIdentifier] = variable;
        }
    }

    private ServiceResult HandleTagRead(ISystemContext context, NodeState node, ref object value)
    {
        lock (_syncRoot)
        {
            if (node is not BaseDataVariableState variable || variable.NodeId?.Identifier is not string nodeIdentifier)
            {
                return StatusCodes.BadNodeIdUnknown;
            }

            _runtime.RegisterOneShotDemand(nodeIdentifier);
            if (_runtime.TryGetCachedValue(nodeIdentifier, out var cached))
            {
                value = cached.Value ?? GetDefaultValueByNode(nodeIdentifier);
                return ServiceResult.Good;
            }

            value = variable.Value ?? GetDefaultValueByNode(nodeIdentifier);
        }

        return ServiceResult.Good;
    }

    private ServiceResult HandleTagWrite(ISystemContext context, NodeState node, ref object value)
    {
        lock (_syncRoot)
        {
            if (node is not BaseDataVariableState variable || variable.NodeId?.Identifier is not string nodeIdentifier)
            {
                return StatusCodes.BadNodeIdUnknown;
            }

            var writeResult = _runtime.WriteAsync(nodeIdentifier, value, CancellationToken.None).GetAwaiter().GetResult();
            if (!writeResult.Success)
            {
                return new ServiceResult(StatusCodes.Bad, "Write rejected by driver runtime.", writeResult.Error, (Exception?)null);
            }

            variable.Value = value;
            variable.Timestamp = DateTime.UtcNow;
            variable.StatusCode = StatusCodes.Good;
            variable.ClearChangeMasks(context, false);
            return ServiceResult.Good;
        }
    }

    private void SyncFromCache()
    {
        lock (_syncRoot)
        {
            foreach (var (nodeIdentifier, variable) in _variablesByNodeId)
            {
                if (!_runtime.TryGetCachedValue(nodeIdentifier, out var snapshot))
                {
                    continue;
                }

                var incomingValue = snapshot.Value ?? GetDefaultValueByNode(nodeIdentifier);
                if (Equals(variable.Value, incomingValue) &&
                    variable.StatusCode == ParseQuality(snapshot.Quality) &&
                    variable.Timestamp == snapshot.TimestampUtc)
                {
                    continue;
                }

                variable.Value = incomingValue;
                variable.Timestamp = snapshot.TimestampUtc;
                variable.StatusCode = ParseQuality(snapshot.Quality);
                variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    private object GetDefaultValueByNode(string nodeIdentifier)
    {
        if (_runtime.TagsByNodeId.TryGetValue(nodeIdentifier, out var tag))
        {
            return GetDefaultValue(tag.DataType);
        }

        return 0d;
    }

    private static StatusCode ParseQuality(string quality)
    {
        return string.Equals(quality, "Good", StringComparison.OrdinalIgnoreCase)
            ? StatusCodes.Good
            : StatusCodes.UncertainNoCommunicationLastUsableValue;
    }

    private FolderState GetOrCreateDeviceFolder(string channelName, string deviceName)
    {
        var channelFolder = GetOrCreateChannelFolder(channelName);
        var key = $"{channelName}/{deviceName}";
        if (_deviceFolders.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var deviceFolder = CreateFolder(
            channelFolder,
            deviceName,
            new NodeId($"DriverGateway.{channelName}.{deviceName}", _namespaceIndex));
        channelFolder.AddChild(deviceFolder);
        AddPredefinedNode(SystemContext, deviceFolder);
        _deviceFolders[key] = deviceFolder;
        return deviceFolder;
    }

    private FolderState GetOrCreateChannelFolder(string channelName)
    {
        if (_rootFolder is null)
        {
            throw new InvalidOperationException("Root folder is not initialized.");
        }

        if (_channelFolders.TryGetValue(channelName, out var existing))
        {
            return existing;
        }

        var channelFolder = CreateFolder(
            _rootFolder,
            channelName,
            new NodeId($"DriverGateway.{channelName}", _namespaceIndex));
        _rootFolder.AddChild(channelFolder);
        AddPredefinedNode(SystemContext, channelFolder);
        _channelFolders[channelName] = channelFolder;
        return channelFolder;
    }

    private FolderState CreateFolder(NodeState? parent, string name, NodeId nodeId)
    {
        return new FolderState(parent)
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
    }

    private static NodeId ResolveOpcDataType(TagDataType dataType)
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

    private static object GetDefaultValue(TagDataType dataType)
    {
        return dataType switch
        {
            TagDataType.Double => 0d,
            TagDataType.Float => 0f,
            TagDataType.Boolean => false,
            TagDataType.Int16 => (short)0,
            TagDataType.UInt16 => (ushort)0,
            TagDataType.Word => (ushort)0,
            TagDataType.Int32 => 0,
            TagDataType.UInt32 => 0u,
            TagDataType.DWord => 0u,
            TagDataType.String => string.Empty,
            _ => 0d
        };
    }

    private static string? ExtractNodeIdentifier(NodeHandle handle, MonitoredItem monitoredItem)
    {
        if (handle.NodeId?.Identifier is string fromHandle)
        {
            return fromHandle;
        }

        return null;
    }
}
