# DriverGateway V1 Acceptance Checklist

## Bağlantı ve İzolasyon

- [ ] Channel A bağlantısı düşerken Channel B publish ve read akışı kesilmiyor.
- [ ] Retry exponential backoff + jitter loglarından doğrulanıyor.
- [ ] Demand yokken sağlık kontrolü periyodik devam ediyor.

## Demand Bazlı Okuma

- [ ] Subscription açılan tagler active demand setine giriyor.
- [ ] Subscription kapanınca demand setinden çıkıyor.
- [ ] One-shot read cache-first cevap dönüyor ve async refresh tetikleniyor.
- [ ] Talep olmayan tagler için `ReadAsync` planına tag eklenmiyor.

## Batch Optimizasyonu

- [ ] Modbus: ardışık register aralıkları tek batch olarak üretiliyor.
- [ ] S7: aynı DB içindeki ardışık byte aralıkları tek batch olarak üretiliyor.

## Write Politikası

- [ ] `immediate` taglerde yazma doğrudan cihaza gönderiliyor.
- [ ] `queued` taglerde yazma worker kuyruğuna alınıyor.
- [ ] Başarılı write sonrası cache/node değeri güncelleniyor.

## Plugin Yükleme

- [ ] Startup'ta `plugins` klasörü taranıyor.
- [ ] Geçersiz DLL host'u düşürmeden hata logluyor.
- [ ] Geçerli plugin `DriverType` ile registry'ye giriyor.
