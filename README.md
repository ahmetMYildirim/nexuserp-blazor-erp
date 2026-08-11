# NexusERP

**Çok kiracılı (multi-tenant) ön muhasebe ve abonelik faturalandırma sistemi.**

.NET 9 · Blazor Server · MudBlazor · EF Core 9 · PostgreSQL 17 · Clean Architecture

---

## Öne çıkan özellikler

**KDV / tevkifat / iskonto hesaplama motoru**
Satır bazlı yuvarlama (GİB standardı), kuruş kaybı olmayan orantısal belge iskontosu dağıtımı.
100 TL'yi üç satıra bölerken `33,33 × 3 = 99,99` değil, tam `100,00` dağıtılır.

**Atomik fatura numaralandırma**
`INSERT ... ON CONFLICT ... RETURNING` ile tek ifadede üretim. 50 paralel istekte
çakışmasız ve **boşluksuz** seri — testle kanıtlı (`SELECT MAX(no)+1` yarış koşuluna açıktır).

**Idempotent abonelik faturalandırma**
Aynı abonelik + aynı dönem için ikinci fatura üretilmez. Garanti iş mantığında değil,
`(subscription_id, period_start)` **unique index**'inde — yarış koşulunda uygulama katmanı
yanılır, veri tabanı kısıtı yanılmaz.

**"Ayın son günü" problemi çözülü**
31 Ocak'ta başlayan aylık abonelik: 31 Oca → 28 Şub → **31 Mar** (28 Mar değil).
Çapa günü ayrı saklanır; `AddMonths` tek başına günü kalıcı kaydırır.

**Tenant izolasyonu**
EF Core global query filter, reflection ile tüm `ITenantScoped` entity'lere merkezî uygulanır.
Entegrasyon testiyle doğrulanmıştır (A tenant'ı B'nin verisini göremez).

**Cari hesap defteri**
Bakiye kolonda tutulmaz, hareketlerden `SUM` ile hesaplanır. Yanlış kayıt silinmez —
ters kayıtla düzeltilir, muhasebe izi korunur.

**Yaşlandırma raporu**
30/60/90 gün kovaları, tek SQL sorgusu (koşullu `SUM`'lar `CASE WHEN` olarak çalışır),
Excel çıktısı formüllü.

**Fatura PDF'i** — QuestPDF, tevkifat dipnotu, ETTN, Türkçe font desteği.

**e-Fatura (UBL-TR 1.2) XML üretimi**
GİB formatında XML: `CustomizationID=TR1.2`, ETTN, KDV oranı bazında ayrı `TaxSubtotal`,
tevkifat için `WithholdingTaxTotal`, UN/ECE Rec.20 birim kodları. Entegratör bağlantısı
`IEInvoiceGateway` arayüzünün arkasında — sözleşme imzalandığında yalnızca o sınıf yazılır.

**REST API** (`NexusErp.Api`)
JWT kimlik doğrulama, rol bazlı yetki, hız sınırlama, Scalar/OpenAPI dokümantasyonu.
Application katmanına **hiç dokunmadan** eklendi — aynı servisler hem Blazor'dan hem
REST'ten çağrılıyor, iş kuralları tek yerde.

---

## Hızlı başlangıç

```bash
docker compose up -d
dotnet run --project src/NexusErp.Web
```

→ http://localhost:5283 · pgAdmin: http://localhost:5050 (`admin@nexuserp.com` / `admin`)

### Demo hesapları — parola hepsi için `Demo!2026`

| E-posta | Rol | Yetki |
|---|---|---|
| `admin@nexusdemo.com.tr` | Admin | Her şey |
| `muhasebe@nexusdemo.com.tr` | Muhasebe | Fatura kesme, tahsilat, raporlar |
| `satis@nexusdemo.com.tr` | Satış | Cari ve fatura oluşturma — **tahsilat yok** |
| `bakis@nexusdemo.com.tr` | Görüntüleyici | Salt okuma |

Rolleri karşılaştırmak için Satış ile girip `/tahsilatlar`'a gitmeyi dene — "yetkisiz erişim"
sayfası gelir. Fatura listesinde "kes/sil" butonları da o rolde görünmez.

Uygulamayı da container'da çalıştırmak için:

```bash
docker compose --profile full up -d --build
```

İlk açılışta migration'lar uygulanır ve demo verisi üretilir: 1 firma, 5 cari, 4 ürün,
4 abonelik planı, farklı gecikme kovalarına düşen faturalar ve tahsilatlar.

---

## Mimari

```
NexusErp.Web (Blazor Server)
        ↓
NexusErp.Application  ── servisler, DTO'lar, arayüzler
        ↓
NexusErp.Domain       ── entity, value object, iş kuralı · NuGet bağımlılığı YOK
        ↑
NexusErp.Infrastructure ── EF Core, PostgreSQL, QuestPDF, ClosedXML
```

Bağımlılık yönü tek: dışarıdan içeri. Hesaplama motoru Domain'de **saf fonksiyon** olduğu için
arayüzdeki canlı toplam önizlemesi ile sunucudaki kayıt **aynı kodu** çağırır — iki hesap
birbirinden ayrışamaz.

### Kayda değer kararlar

| Karar | Gerekçe |
|---|---|
| MediatR yok | v13+ ticari lisans; düz servisle stack trace okunabilir kalıyor |
| Repository yok | `DbSet<T>` zaten repository, `DbContext` zaten Unit of Work |
| `decimal(18,4)` + `AwayFromZero` | .NET varsayılanı banker's rounding, faturada kuruş farkı yapar |
| Paylaşımlı tablo + query filter | Tek kişilik ekipte migration maliyeti belirleyici |
| Soft delete + partial unique index | Muhasebe verisi silinmez; silinmiş kod index'i işgal etmemeli |
| `DateOnly` vs `DateTimeOffset` | Fatura tarihi takvim günüdür, saat dilimi taşımaz |

---

## Test

```bash
dotnet test
```

**95 test.** Hesaplama motoru saf birim testleriyle (milisaniyeler), servisler
**Testcontainers** üzerinde gerçek PostgreSQL ile. InMemory sağlayıcı kullanılmadı —
partial index ve precision davranışını taklit edemez, testler geçer ama üretimde patlar.

Öne çıkanlar:
- 50 paralel istekte fatura numarası çakışması olmadığı
- Bir tenant'ın diğerinin verisini göremediği
- Aynı dönem için ikinci abonelik faturası üretilmediği
- Belge iskontosunda kuruş kaybı olmadığı

---

## REST API

```bash
dotnet run --project src/NexusErp.Api
```

→ http://localhost:5299/scalar/v1 (interaktif dokümantasyon)

```bash
# 1) Jeton al
curl -X POST http://localhost:5299/api/auth/token \
  -H "Content-Type: application/json" \
  -d '{"email":"muhasebe@nexusdemo.com.tr","password":"Demo!2026"}'

# 2) Fatura kes ve e-Fatura XML'ini çek
curl -X POST http://localhost:5299/api/faturalar/{id}/kes -H "Authorization: Bearer $TOKEN"
curl http://localhost:5299/api/faturalar/{id}/ubl -H "Authorization: Bearer $TOKEN"
```

| Uç | Açıklama |
|---|---|
| `POST /api/auth/token` | JWT jetonu (web ile aynı kullanıcı ve roller) |
| `GET/POST /api/cariler` | Cari listesi ve kaydı |
| `GET/POST /api/faturalar` | Fatura listesi, taslak oluşturma |
| `POST /api/faturalar/{id}/kes` | Atomik numara ile faturayı kes |
| `GET /api/faturalar/{id}/pdf` · `/ubl` | PDF ve e-Fatura XML çıktısı |
| `POST /api/tahsilatlar` | Tahsilat + FIFO eşleştirme |
| `GET /api/tahsilatlar/yaslandirma` | Yaşlandırma raporu |
| `POST /api/abonelikler/faturalandir` | **Idempotent** — zamanlanmış görevden güvenle çağrılır |

Yetki API'de de geçerli: Satış rolüyle `POST /faturalar/{id}/kes` → **403**.

## Performans — ölçüldü, iddia edilmedi

100.000 fatura / 500 cari ile (`Bogus` + toplu `INSERT`, PostgreSQL 17):

| Sorgu | Süre | Plan |
|---|---|---|
| Yaşlandırma raporu (tüm açık faturaları gruplar) | **80 ms** | `Seq Scan` + `HashAggregate` |
| Tek carinin açık faturaları (seçici) | **2 ms** | `Bitmap Index Scan` |

**Hipotez:** yaşlandırma raporuna kapsayıcı index eklemek hızlandırır.
**Ölçüm:** hızlandırmadı — *anlamlı fark yok*.

**Neden:** sorguda seçici filtre yok, tablonun tamamı okunuyor; PostgreSQL doğru şekilde
sıralı tarama seçiyor. Index eklemek okumayı hızlandırmaz, yalnızca yazma maliyetini artırır.
Seçici sorgu ise EF'in yabancı anahtar için ürettiği index'i zaten kullanıyor.

**Karar: index eklenmedi.** 1M satır ölçeğinde doğru çözüm gece yenilenen bir
materialized view olur, index değil.

```bash
dotnet test --filter FullyQualifiedName~AgingReportBenchmark   # ölçümü tekrarla
```

## Bilinçli olarak kapsam dışı

Stok/depo yönetimi, muhasebe fişi ve tek düzen hesap planı, gerçek e-Fatura entegratör
bağlantısı (UBL-TR XML üretimi hazır değil), alış faturası, çoklu döviz ve kur farkı,
kimlik doğrulama (tenant şu an yapılandırmadan geliyor).

Amaç genişlik değil derinlikti: faturalandırma ve abonelik motoru üretim kalitesinde yazıldı.
Genişleme planı ve teknik borç listesi: `docs/13-tam-surume-gecis.md`

---

## Ekran haritası

| Sayfa | İçerik |
|---|---|
| `/` | Dashboard: açık alacak, vadesi geçen, MRR/ARR, ciro-tahsilat grafiği, durum dağılımı |
| `/cariler` | Cari kartlar, VKN/TCKN checksum doğrulamalı |
| `/cari-ekstre` | Devir satırlı, yürüyen bakiyeli ekstre |
| `/faturalar` · `/faturalar/yeni` | Fatura listesi ve canlı toplamlı editör, PDF indirme |
| `/urunler` | Ürün/hizmet kataloğu, KDV ve tevkifat oranları |
| `/planlar` · `/abonelikler` | Abonelik planları, MRR kartları, "Şimdi Faturalandır" |
| `/tahsilatlar` | Tahsilat kaydı, FIFO eşleştirme, ters kayıtla iptal |
| `/yaslandirma` | 30/60/90 gün kovaları, Excel çıktısı |
