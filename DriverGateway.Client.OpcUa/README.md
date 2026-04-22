# DriverGateway.Client.OpcUa

JSON tabanli OPC UA client uygulamasidir.

## Calistirma

```powershell
dotnet run --project DriverGateway.Client.OpcUa/DriverGateway.Client.OpcUa.csproj
```

Opsiyonel:

```text
--config <path-to-client.config.json>
--apply-writes
```

## Davranis

1. Config startup'ta bir kez yuklenir.
2. Security None endpoint'e baglanir.
3. `readNodes` icin one-shot read yapar.
4. `subscribeNodes` icin subscription baslatir.
5. `writes` listesi sadece `--apply-writes` verilirse uygulanir.
