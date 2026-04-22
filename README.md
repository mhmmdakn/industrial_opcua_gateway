# Industrial Protocol Gateway over OPC UA

Modular OPC UA gateway that exposes industrial protocol data through a unified OPC UA server surface.

The repository includes:

- A plugin-based gateway host (`DriverGateway.Host.OpcUa`)
- A JSON-configurable OPC UA client (`DriverGateway.Client.OpcUa`)
- Protocol plugins (`MockSimulator`, `S7Comm`, `ModbusTcp`)
- Core contracts and runtime abstractions
- Smoke tests for demand tracking, batching, Modbus behavior, and runtime safety checks

## Key Features

- Plugin architecture for protocol drivers (`IDriverPlugin`, `IChannelRuntime`)
- Hierarchical configuration model: `drivers -> channels -> devices -> tags`
- Demand-aware read behavior (subscription + one-shot cache-first read)
- Retry policy with exponential backoff and jitter
- Write policies (`immediate` and `queued`)
- Batch planning for S7 and Modbus address ranges

## Solution Layout

| Project | Purpose |
|---|---|
| `DriverGateway.Host.OpcUa` | Main OPC UA gateway host/server runtime |
| `DriverGateway.Client.OpcUa` | Client app for read/subscribe/write scenarios from JSON config |
| `DriverGateway.Core` | Shared domain models and core services |
| `DriverGateway.Plugins.Abstractions` | Plugin contracts/interfaces |
| `DriverGateway.Plugins.MockSimulator` | Mock driver plugin for local simulation |
| `DriverGateway.Plugins.S7Comm` | Siemens S7 plugin |
| `DriverGateway.Plugins.ModbusTcp` | Modbus TCP plugin |
| `DriverGateway.SmokeTests` | Console-based smoke tests |
| `OpcUaServerApp`, `OpcUaServerDynamicApp`, `OpcUaClientApp` | Supporting/sample OPC UA apps |

## Requirements

- .NET 8 SDK
- Windows (validated environment), PowerShell

## Quick Start

```powershell
dotnet restore .\OpcUaSample.sln --configfile .\NuGet.Config
dotnet build .\OpcUaSample.sln
```

## Run Gateway Host

```powershell
dotnet run --project .\DriverGateway.Host.OpcUa\DriverGateway.Host.OpcUa.csproj
```

Optional arguments:

```text
--endpoint <opc.tcp://host:port/path>
--name <ApplicationName>
--config <path-to-gateway.config.json>
--plugins <plugins-folder>
```

Default endpoint:

```text
opc.tcp://localhost:4842/UA/DriverGateway
```

Default gateway config file:

```text
DriverGateway.Host.OpcUa/gateway.config.json
```

## Run OPC UA Client

```powershell
dotnet run --project .\DriverGateway.Client.OpcUa\DriverGateway.Client.OpcUa.csproj
```

Optional arguments:

```text
--config <path-to-client-config>
--apply-writes
```

Note: current default client config in code is `client.write.json`.

## Run Smoke Tests

```powershell
dotnet run --project .\DriverGateway.SmokeTests\DriverGateway.SmokeTests.csproj
```

## Configuration Model (V2)

Configuration hierarchy in `gateway.config.json`:

- `drivers[]`
- `channels[]`
- `devices[]`
- `tags[]`

Common tag fields:

- `name`
- `dataType`
- `address`
- `scanClass`
- `write.mode`

## Plugin Development

Use the template and checklist in:

- `DriverGateway.PluginTemplate.md`
- `DriverGateway.AcceptanceChecklist.md`

New plugins should be added as `DriverGateway.Plugins.<Protocol>` class libraries and registered via the shared plugin contract.

## Notes

- In `Debug`, untrusted OPC UA certificates are auto-accepted for easier local development.
- In `Release`, certificate trust should be managed explicitly for production use.
