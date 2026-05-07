# DriverGateway.LoadTests.ModbusTcp

Modbus TCP için basit ama genişletilebilir yük testi uygulaması.

## Özellikler

- Çoklu sanal client ile eşzamanlı yük üretimi
- Ağırlıklı operasyon karışımı:
  - `FC03` Read Holding Registers
  - `FC04` Read Input Registers
  - `FC06` Write Single Register
  - `FC16` Write Multiple Registers
- Stage bazlı yük profili (`warmup`, `baseline`, `ramp`, `spike`, `soak`)
- Lineer veya anlık client artırma/azaltma
- `p50/p95/p99` gecikme ve operasyon bazlı başarı/hata özeti
- Opsiyonel local in-process Modbus server

## Çalıştırma

```powershell
dotnet run --project .\DriverGateway.LoadTests.ModbusTcp\DriverGateway.LoadTests.ModbusTcp.csproj
```

Kısa smoke koşu (örnek senaryoyu 10x kısaltarak):

```powershell
dotnet run --project .\DriverGateway.LoadTests.ModbusTcp\DriverGateway.LoadTests.ModbusTcp.csproj -- --duration-scale 0.1
```

Harici hedef override:

```powershell
dotnet run --project .\DriverGateway.LoadTests.ModbusTcp\DriverGateway.LoadTests.ModbusTcp.csproj -- --scenario .\DriverGateway.LoadTests.ModbusTcp\scenario.sample.json --endpoint 192.168.1.50:502 --no-local-server
```

## Senaryo Dosyası

Varsayılan: `scenario.sample.json`

Temel alanlar:

- `endpoint`: `host:port` veya `modbus://host:port`
- `useLocalServer`: `true` ise aynı process içinde loopback Modbus server açar
- `mix`: işlem ağırlıkları
- `stages`: süre, hedef client sayısı ve request interval
