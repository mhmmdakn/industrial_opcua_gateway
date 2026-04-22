namespace DriverGateway.Core.Interfaces;

public interface IDemandTracker
{
    void AddSubscriptionDemand(string nodeIdentifier);
    void RemoveSubscriptionDemand(string nodeIdentifier);
    void RegisterOneShotDemand(string nodeIdentifier, TimeSpan ttl);
    IReadOnlySet<string> GetActiveDemand(DateTime utcNow);
}
