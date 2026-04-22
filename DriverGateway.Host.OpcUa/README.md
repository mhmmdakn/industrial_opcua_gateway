# DriverGateway.Host.OpcUa

Kepware-benzeri V1 altyapının OPC UA host uygulamasıdır.

## Çalıştırma

```powershell
dotnet run --project DriverGateway.Host.OpcUa/DriverGateway.Host.OpcUa.csproj
```

Opsiyonel argümanlar:

```text
--endpoint <opc.tcp://host:port/path>
--name <ApplicationName>
--config <path-to-gateway.config.json>
--plugins <plugins-folder>
```

## Config V2 Özeti

- Hiyerarşi: `drivers[] -> channels[] -> devices[] -> tags[]`
- Tag alanları:
  - `name`, `dataType`, `address`, `scanClass`, `write.mode`
- Driver/Channel alanları:
  - `type`, `endpoint`, `retry`, `scanClasses`, `settings`

Varsayılan örnek dosya: `gateway.config.json`
