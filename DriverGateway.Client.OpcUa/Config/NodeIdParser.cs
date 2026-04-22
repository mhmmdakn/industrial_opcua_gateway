using Opc.Ua;

namespace DriverGateway.Client.OpcUa.Config;

internal static class NodeIdParser
{
    public static bool TryParse(string rawNodeId, out NodeId nodeId)
    {
        nodeId = NodeId.Null;

        if (string.IsNullOrWhiteSpace(rawNodeId))
        {
            return false;
        }

        try
        {
            nodeId = NodeId.Parse(rawNodeId);
            return nodeId != NodeId.Null;
        }
        catch
        {
            return false;
        }
    }
}
