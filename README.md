# NexusERP

**Multi-tenant ön muhasebe ve abonelik faturalandırma sistemi.**

.NET 9 · Blazor Server · MudBlazor · EF Core 9 · PostgreSQL 17 · Clean Architecture

---

## Hızlı başlangıç

```bash
docker compose up -d
dotnet run --project src/NexusErp.Web
```

→ http://localhost:5283 · pgAdmin: http://localhost:5050 (`admin@nexuserp.com` / `admin`)

Uygulamayı da container'da çalıştırmak için: `docker compose --profile full up -d --build`

İlk açılışta migration'lar uygulanır ve demo verisi üretilir: 5 cari, 4 ürün, 4 abonelik
planı, farklı gecikme kovalarına düşen faturalar ve tahsilatlar.

### Demo hesapları — parola hepsi için `Demo!2026`

| E-posta | Rol | Yetki |
|---|---|---|
| `admin@nexusdemo.com.tr` | Admin | Her şey |
| `muhasebe@nexusdemo.com.tr` | Muhasebe | Fatura kesme, tahsilat, raporlar |
| `satis@nexusdemo.com.tr` | Satış | Cari ve fatura oluşturma — tahsilat yok |
| `bakis@nexusdemo.com.tr` | Görüntüleyici | Salt okuma |

---

## Teknik olarak kayda değer kısımlar

**KDV / tevkifat / iskonto motoru.** Satır bazlı yuvarlama (GİB standardı) ve kuruş kaybı
olmayan orantısal belge iskontosu dağıtımı: 100 TL üç satıra bölünürken
`33,33 × 3 = 99,99` değil, tam `100,00`. Motor Domain'de saf fonksiyon olduğu için
arayüzdeki canlı önizleme ile sunucudaki kayıt **aynı kodu** çağırır; iki hesap ayrışamaz.

**Atomik fatura numaralandırma.** `INSERT ... ON CONFLICT ... RETURNING` ile tek ifadede.
50 paralel istekte çakışmasız ve boşluksuz seri — testle kanıtlı.
`SELECT MAX(no)+1` yarış koşuluna açıktır.

**Idempotent abonelik faturalandırma.** Aynı abonelik + aynı dönem için ikinci fatura
üretilmez. Garanti iş mantığında değil `(subscription_id, period_start)` unique index'inde:
yarış koşulunda uygulama katmanı yanılır, veri tabanı kısıtı yanılmaz.
Faturalar saatlik çalışan bir arka plan işçisi tarafından kesilir.

**"Ayın son günü" problemi.** 31 Ocak'ta başlayan aylık abonelik:
31 Oca → 28 Şub → **31 Mar** (28 Mar değil). Çapa günü ayrı saklanır;
`AddMonths` tek başına günü kalıcı kaydırır.

**Tenant izolasyonu.** EF Core global query filter, reflection ile tüm `ITenantScoped`
entity'lere merkezî uygulanır. Entegrasyon testiyle doğrulanmıştır.

**Denetim kaydı.** Her değişiklik `SaveChanges` üzerinden JSON olarak yazılır: kim,
ne zaman, hangi alanı neyden neye çevirdi. `/denetim` sayfasından filtrelenebilir.

**e-Fatura (UBL-TR 1.2).** GİB formatında XML: `CustomizationID=TR1.2`, ETTN, KDV oranı
bazında ayrı `TaxSubtotal`, tevkifat için `WithholdingTaxTotal`, UN/ECE Rec.20 birim
kodları. Entegratör bağlantısı `IEInvoiceGateway` arkasında — sözleşme imzalanınca
yalnızca o sınıf yazılır.

Ayrıca: cari hesap defteri (bakiye kolonda tutulmaz, hareketlerden hesaplanır),
30/60/90 gün yaşlandırma raporu, fatura PDF'i (QuestPDF), formüllü Excel çıktısı.

---

## Mimari

```
NexusErp.Web (Blazor) ─┐
                       ├─→ Infrastructure ─→ Application ─→ Domain
NexusErp.Api (REST)  ──┘      EF Core          servisler      iş kuralları
                              PostgreSQL       DTO'lar        NuGet bağımlılığı YOK
```

Bağımlılık yönü tek: dışarıdan içeri. Web ve API **aynı** Application servislerini kullanır;
aralarındaki tek fark kimlik kaynağıdır (çerez vs JWT).

| Karar | Gerekçe |
|---|---|
| MediatR yok | v13+ ticari lisans; düz servisle stack trace okunabilir kalıyor |
| Repository yok | `DbSet<T>` zaten repository, `DbContext` zaten Unit of Work |
| `decimal(18,4)` + `AwayFromZero` | .NET varsayılanı banker's rounding, faturada kuruş farkı yapar |
| Soft delete + partial unique index | Muhasebe verisi silinmez; silinmiş kod index'i işgal etmemeli |
| `IDbContextFactory` | Blazor'da scoped servis devre ömrü boyunca yaşar; her işlem taze context açar |

---

## Test

```bash
dotnet test
```

**122 test.** Hesaplama motoru saf birim testleriyle (milisaniyeler), servisler
**Testcontainers** üzerinde gerçek PostgreSQL ile. InMemory sağlayıcı kullanılmadı —
partial index, ICU sıralaması ve precision davranışını taklit edemez; testler geçer,
üretimde patlar.

Öne çıkanlar: 50 paralel istekte numara çakışması olmadığı · bir tenant'ın diğerinin
verisini göremediği · aynı dönem için ikinci abonelik faturası üretilmediği · belge
iskontosunda kuruş kaybı olmadığı.

---

## REST API

```bash
dotnet run --project src/NexusErp.Api
```

→ http://localhost:5299/scalar/v1 (interaktif dokümantasyon)

JWT kimlik doğrulama, rol bazlı yetki, hız sınırlama. Application katmanına **hiç
dokunmadan** eklendi. Yetki API'de de geçerli: Satış rolüyle
`POST /api/faturalar/{id}/kes` → **403**.

Başlıca uçlar: `/api/auth/token` · `/api/cariler` · `/api/faturalar` (+ `/kes`, `/pdf`,
`/ubl`) · `/api/tahsilatlar` (+ `/yaslandirma`) · `/api/abonelikler/faturalandir`
(idempotent, zamanlanmış görevden güvenle çağrılır).

---

## Performans

100.000 fatura / 500 cari ile: yaşlandırma raporu **80 ms** (`Seq Scan` + `HashAggregate`),
tek carinin açık faturaları **2 ms** (`Bitmap Index Scan`).

Hipotez yaşlandırma raporuna kapsayıcı index eklemenin hızlandıracağıydı; ölçüm anlamlı
fark göstermedi. Sorguda seçici filtre yok, tablonun tamamı okunuyor — index okumayı
hızlandırmaz, yalnızca yazma maliyetini artırır. **Karar: index eklenmedi.**

```bash
dotnet test --filter FullyQualifiedName~AgingReportBenchmark
```

---

## Kapsam dışı

Stok/depo, muhasebe fişi ve tek düzen hesap planı, alış faturası, çoklu döviz ve kur farkı,
gerçek entegratör bağlantısı (UBL-TR XML üretimi hazır, bağlantı değil).

Amaç genişlik değil derinlikti: faturalandırma ve abonelik motoru üretim kalitesinde yazıldı.
