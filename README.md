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

---

## Hızlı başlangıç

```bash
docker compose up -d
dotnet run --project src/NexusErp.Web
```

→ http://localhost:5283 · pgAdmin: http://localhost:5050 (`admin@nexuserp.com` / `admin`)

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
