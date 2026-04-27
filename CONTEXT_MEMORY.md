# OPC_UA Workspace Context Memory

Bu dosya, farkli sohbetlerde hizli baglam geri cagirma icin hazirlanmistir.
Son guncelleme: 2026-04-27

## 1) Repo ve ana projeler

- `DriverGateway.Host.OpcUa`
  - Kepware-benzeri host.
  - Varsayilan endpoint: `opc.tcp://localhost:4840/UA/DriverGateway`
  - Config: `DriverGateway.Host.OpcUa/gateway.config.json`
- `DriverGateway.Client.OpcUa`
  - Config tabanli OPC UA client (read/subscribe/write).
  - Config: `DriverGateway.Client.OpcUa/client.config.json`
  - `writes[]` sadece `--apply-writes` parametresi ile uygulanir.
- `DriverGateway.Plugins.ModbusTcp`
  - Modbus TCP plugin.
  - `wordOrder` channel-level ayardir.
- `DriverGateway.Testing.ModbusServer` (yeni)
  - Scriptlenebilir test Modbus server projedir.
  - Dosya: `DriverGateway.Testing.ModbusServer/ScriptedModbusServer.cs`
- `DriverGateway.SmokeTests`
  - Modbus/S7 davranis testlerini calistirir.
  - Artik ayri test Modbus server projesini kullanir.
- `OpcUaServerApp`
  - Basit sample server (statik demo node'lar).
- `OpcUaServerDynamicApp`
  - JSON tabanli dinamik node yonetimi (Channel > Device > Tag).
  - Config: `OpcUaServerDynamicApp/nodes.json`
  - Varsayilan endpoint: `opc.tcp://localhost:4840/UA/SampleServer`
  - Hot reload: add + update + remove
  - Provider'lar: `constant`, `counter`, `random`, `sine`
  - Write policy: TTL override (default 10 sn)

## 2) Son yapilan kritik gelistirmeler

### 2.1 Dynamic server (mevcut)

- Solution'a proje eklendi: `OpcUaServerDynamicApp`
- Eklendi:
  - `OpcUaServerDynamicApp/Program.cs`
  - `OpcUaServerDynamicApp/DynamicConfiguration.cs`
  - `OpcUaServerDynamicApp/DynamicProviders.cs`
  - `OpcUaServerDynamicApp/DynamicNodeManager.cs`
  - `OpcUaServerDynamicApp/nodes.json`
- Guvenlik secenekleri ve user token policy listesi sample server ile uyumlu tutuldu.

### 2.2 Modbus reconnect stabilite gelistirmeleri (yeni)

- Dosya: `DriverGateway.Host.OpcUa/Runtime/ChannelWorker.cs`
- Yapilanlar:
  - `WriteImmediateAsync` disconnected durumda once reconnect dener, sonra write yapar.
  - Loop catch blogunda reconnect denemesi hata verirse worker dusmez; retry dongusu ayakta kalir.
  - Loop basinda reconnect kontrolu korunur.
  - Baslangicta loop oncesi tek seferlik connect kaldirildi.
    - Sebep: host acilisinda server kapaliysa worker task'inin olmesini engellemek.
  - Demand tag secimi optimize edildi:
    - Tum `_tagsByNodeId` taramasi yerine `activeDemand` key lookup ile secim yapilir.

- Dosya: `DriverGateway.Plugins.ModbusTcp/ModbusTcpDriverPlugin.cs`
- Yapilanlar:
  - `InvalidOperationException("...disconnected")` read/write icin comm-state hatasi gibi ele aliniyor.
  - Bu yolda `MarkDisconnected(...)` cagrilarak state tutarliligi korunuyor.

- Dosya: `DriverGateway.Host.OpcUa/Properties/AssemblyInfo.cs`
  - Eklendi: `[assembly: InternalsVisibleTo("DriverGateway.SmokeTests")]`

## 3) Modbus `wordOrder` notlari

- Ayar yeri: `gateway.config.json` icinde ilgili Modbus channel `settings`.
- Gecerli degerler:
  - `high-word-first` (default)
  - `low-word-first`
- Plugin parse noktasi:
  - `DriverGateway.Plugins.ModbusTcp/ModbusTcpDriverPlugin.cs`
  - `ParseWordOrder(...)`
- `wordOrder` ozellikle cok-register tiplerde etkilidir (`Float/Int32/UInt32/Double`).
- Tek register tiplerde (`Boolean/Int16/UInt16/Word`) etkisi yoktur.

## 4) Cihaza write akislar

### A) DriverGateway tarafinda onerilen yontem (mevcut client)

1. `DriverGateway.Client.OpcUa/client.config.json` veya ayri config dosyasinda `writes` doldur.
2. Komut:

```powershell
dotnet run --project .\DriverGateway.Client.OpcUa\DriverGateway.Client.OpcUa.csproj -- --config .\DriverGateway.Client.OpcUa\client.config.json --apply-writes
```

Notlar:
- `type` alaninda destekli tipler: `Boolean`, `Int32`, `Double`, `Float`, `String`
- Config dosyasi tam JSON obje olmali; sadece `"writes": [...]` tek basina gecerli degildir.

### B) Basit sample client ile write

```powershell
dotnet run --project .\OpcUaClientApp\OpcUaClientApp.csproj -- --endpoint opc.tcp://localhost:4840/UA/DriverGateway --write-node 'ns=2;s=MbCh1.PowerMeter01.BreakerClosed' --write-value true
```

## 5) Baglanti test stratejisi (guncel)

### 5.1 Ayrica olusturulan test Modbus server

- Proje: `DriverGateway.Testing.ModbusServer`
- Ozellikler:
  - Scriptli response uretimi
  - Sabit port ile restart senaryosu
  - Read/Write/Exception/Transaction mismatch simulasyonu

### 5.2 Smoke test kapsamindaki reconnect testleri

- Dosya: `DriverGateway.SmokeTests/Program.cs`
- Testler:
  - `Modbus write-only channel reconnects after server restart`
  - `Modbus subscription channel reconnects after server restart`
  - `Modbus connects when server becomes available after startup`

Bu testler su gercek sorunlari dogrular:
- Write-only kopma sonrasi toparlama
- Subscription akisinda kopma ve cache yeniden guncelleme
- Host acilisinda server kapali oldugunda sonradan baglanabilme

## 6) Modbus plugin debug (onerilen)

### Visual Studio

1. Startup project: `DriverGateway.Host.OpcUa`
2. Build config: `Debug`
3. `DriverGateway.Plugins.ModbusTcp` icine breakpoint koy.
4. Host'u F5 ile baslat.
5. `DriverGateway.Client.OpcUa` ile read/write tetikleyerek plugin koduna dus.

### CLI

```powershell
dotnet build .\OpcUaSample.sln
dotnet run --project .\DriverGateway.Host.OpcUa\DriverGateway.Host.OpcUa.csproj
dotnet run --project .\DriverGateway.Client.OpcUa\DriverGateway.Client.OpcUa.csproj -- --apply-writes
```

## 7) Bilinen uyarilar / durum

- `CS0618` uyarilari gorulebilir:
  - `CheckApplicationInstanceCertificate(bool, ushort)`
  - `CoreClientUtils.SelectEndpoint(...)`
- Su anda blocker degil; uygulama calismasina engel olmuyor.
- Bazi komutlarda sandbox nedeniyle NuGet erisim kisiti gorulebilir; gerektiginde elevated calistirma gerekiyor.
- Host process `bin\\Debug\\net8.0` altindaki exe/dll dosyalarini kilitlerse normal build copy adimi hata verebilir.
  - Cozum: test buildlerini gecici `OutDir` ile almak.

## 8) Hizli komut ozeti

### DriverGateway host baslat

```powershell
dotnet run --project .\DriverGateway.Host.OpcUa\DriverGateway.Host.OpcUa.csproj
```

### DriverGateway client (read/sub/write)

```powershell
dotnet run --project .\DriverGateway.Client.OpcUa\DriverGateway.Client.OpcUa.csproj -- --apply-writes
```

### Dynamic OPC UA server baslat

```powershell
dotnet run --project .\OpcUaServerDynamicApp\OpcUaServerDynamicApp.csproj
```

### Smoke testleri (reconnect dahil) calistir

```powershell
dotnet restore .\OpcUaSample.sln --configfile .\NuGet.Config
$out='C:\\Users\\Muhammet AKIN\\Desktop\\repos\\OPC_UA\\_smoke_build_out'
if(Test-Path -LiteralPath $out){ Remove-Item -LiteralPath $out -Recurse -Force }
dotnet build .\DriverGateway.SmokeTests\DriverGateway.SmokeTests.csproj --no-restore -m:1 -v minimal /p:OutDir="$out" /p:UseAppHost=false
dotnet .\_smoke_build_out\DriverGateway.SmokeTests.dll
```

Beklenen: `All smoke tests passed.`

## 9) Sohbetlerde kullanma onerisi

Yeni sohbette su cumle ile baslamak yeterli:

`Lutfen CONTEXT_MEMORY.md dosyasini baz alarak devam et.`
