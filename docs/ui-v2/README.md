# Handoff: NexusERP Web Arayüzü (v2)

## Genel Bakış
NexusERP muhasebe/finans ERP'sinin masaüstü web arayüzü. Blazor tabanlı mevcut uygulamanın (NexusErp deposu) ekranları, piyasadaki ERP kabuk düzenine (SAP Fiori shell bar, NetSuite modül menüsü, Odoo belge sekmeleri) göre yeniden kurgulandı: kalıcı kabuk çubuğu, modül mega-menüsü, açık belge sekmeleri, breadcrumb + sağa hizalı işlem çubuğu, yoğun veri gridleri, alt durum çubuğu.

Kapsam: 21 ekran (pano, 10 liste, 2 mali tablo, fatura editörü, cari ekstre, yaşlandırma raporu, rol yetki matrisi, ayarlar, profil) + koyu tema + bildirim paneli + sayfa bulucu arama.

## Tasarım Dosyaları Hakkında
Bu pakettteki `NexusERP UI v2.dc.html` bir **tasarım referansıdır** — hedeflenen görünümü ve davranışı gösteren, tek dosyada çalışan bir HTML prototipi. Üretim kodu olarak kopyalanmamalıdır.

Yapılacak iş: bu tasarımları hedef kod tabanının kendi ortamında (bu projede **Blazor Server / .NET, MudBlazor + kendi CSS token'ları**) mevcut desenlerle yeniden kurmak. Prototipteki veriler demo verisidir; gerçek servisler (`DashboardService`, `AccountingReportService`, `InvoiceService` vb.) kullanılmalıdır.

## Fidelity
**High-fidelity.** Renkler, tipografi, ölçüler ve etkileşimler nihai değerdir; piksel düzeyinde birebir uygulanmalıdır. Ölçüler yoğun ERP gridleri için özellikle sıkıdır (satır yüksekliği ~22–24px, gövde 12–13px) — büyütülmemelidir.

## Kabuk (tüm ekranlarda ortak)

### 1. Shell bar — yükseklik 44px, zemin `--color-accent-900`, metin `--nx-onbar`
Soldan sağa:
- **NEXUSERP** marka düğmesi (Barlow Condensed 15px, letter-spacing .16em) → Genel Bakış'a gider. Hover: `rgba(242,242,243,.12)`.
- Dikey ayraç 1×20px `rgba(242,242,243,.28)`.
- İki satırlık başlık bloğu: üstte şirket adı (9.5px, uppercase, .12em, opacity .6), altta aktif sayfa adı (Barlow Condensed 14px).
- **Arama** (ortalanmış, `flex:1; max-width:460px`): `type=search`, yükseklik 22px, zemin `rgba(242,242,243,.09)`, kenarlık `rgba(242,242,243,.22)`, placeholder `Ara…  Ctrl+K`. Sayfa bulucudur (veri araması değil): yazınca 320px genişliğinde blueprint açılır liste, satırda sayfa adı + modül grubu; Enter ilk eşleşmeyi açar, Esc temizler, Ctrl/Cmd+K odaklar.
- Sağ küme: dönem çipi (`DÖNEM 2026-08 · AÇIK`, 1px kenarlıklı), tema düğmesi (ay/güneş ikonu + "Koyu"/"Açık"), bildirim çanı + rozet, kullanıcı bloğu.

### 2. Modül çubuğu — 32px, zemin `--color-neutral-200`, alt kenarlık `--color-divider`
- Solda **Modüller** düğmesi (4 kareli grid ikonu) → Genel Bakış.
- Sekmeler: CARİ · SATIŞ · FİNANS · ABONELİK · MUHASEBE · SİSTEM (Barlow Condensed 12px, uppercase, .1em). Aktif/açık sekme: zemin `--color-bg`, 2px alt çizgi `--color-accent`.
- Açılır mega-menü: 480px, blueprint çerçeve + 4 köşe işareti, 2 sütun grid, üstte "<MODÜL> MODÜLÜ" başlığı; her madde başlık (Barlow Condensed 13px) + tek satır açıklama (11px, muted). Hover `--color-accent-100`.

### 3. Belge sekmeleri — 28px (Ayarlar'dan kapatılabilir)
Her sekme: tür rozeti (PANO/LİSTE/BELGE/RAPOR/CARİ/SİSTEM/HESAP — 9px, accent-700), başlık 12px, kirli göstergesi ● (accent-700), kapatma ×. Aktif sekme: zemin `--color-neutral-100`, 2px üst çizgi `--color-accent`. Şerit `overflow-x:auto`, sekmeler `flex:none; white-space:nowrap`. Sağda "+" (aramaya odaklar) ve "Ctrl+K ile sayfa ara" ipucu.

### 4. İşlem çubuğu — 33px, zemin `--color-neutral-100`
Solda breadcrumb (11px, uppercase, .1em, muted; örn. `SATIŞ › FATURALAR › NEX2026000000006`), sağda ekrana özel düğmeler: ilk düğme `btn-primary`, diğerleri `btn-secondary`, yükseklik 23px, üzerinde F-tuşu ipucu (9px, opacity .6) — Ayarlar'dan kapatılabilir.

### 5. Sol kısayol paneli — 186px (kapatılabilir), zemin `--color-neutral-100`
Kısayollar (aktif olan accent-100 zemin + 2px sol çizgi), Dönem listesi (Ağustos AÇIK etiketi), Kuyruklar (Outbox, E-Fatura, son senkron).

### 6. Durum çubuğu — 23px, zemin `--color-accent-900`, 11px
Bağlantı noktası (6×6 accent-300 kare) + sunucu · dönem · yüklenen kayıt · seçim · kısayol ipucu · rol · saat.

## Ekranlar

| id | Ad | Tür |
|---|---|---|
| `dash` | Genel Bakış | Pano |
| `cariler` | Cari Kartlar | Liste |
| `ekstre` | Cari Ekstre / Cari Kart | Detay + form |
| `faturalar` | Faturalar | Liste (çoklu seçim + önizleme) |
| `doc` | Fatura Editörü | Belge |
| `urunler` | Ürün / Hizmet | Liste |
| `tahsilatlar` | Tahsilatlar | Liste |
| `yaslandirma` | Yaşlandırma Raporu | Matris rapor |
| `planlar`, `abonelikler` | Abonelik | Liste |
| `hesap-plani`, `fisler`, `mizan` | Muhasebe | Liste |
| `bilanco`, `gelir-tablosu` | Mali tablo | Statement |
| `denetim`, `sistem-testi` | Sistem | Liste |
| `kullanicilar` | Rol yetki matrisi | Sistem |
| `ayarlar` | Ayarlar | Tercihler |
| `profil` | Profilim | Hesap |

### Genel Bakış (dash)
1. **4 KPI kartı** (blueprint + köşe işaretleri): Açık Alacak, Vadesi Geçen (accent-800), MRR, Bu Ay Ciro (+ 12 çubuklu sparkline, delta satırı). Değer Barlow Condensed 26px, tabular-nums; başlık 10px uppercase .12em muted; ipucu 11px.
2. **Özet göstergeler**: 6 sütunlu, 1px bölmeli grid, 12 mini kart (etiket 10px / değer Barlow Condensed 17px / ipucu 10px). Üstte "12 / 12 kart" + "Kartları Düzenle".
3. **Muhasebe Defteri** paneli: DEFTER DENK etiketi + Mizan/Bilanço bağlantıları, 7 sütunlu mini istatistik şeridi, ardından grafik seçici (Aylık Gelir / Gider · Kümülatif Kâr · Hesap Bakiyeleri).
   - **Çubuk grafik**: solda 40px değer ekseni (88B/66B/44B/22B/0), 150px çizim alanı, 4 kesikli kılavuz + tam çizgili taban, ay başına iki çubuk (gelir `--color-accent-700` dolu; gider `--color-accent-200` dolgu + `--color-accent-600` 1px kenarlık), max genişlik 38px, gruplar arası 22px. Çubukların üstünde rakam **yok** — altta Gelir / Gider / Net satırlı kompakt tablo var.
   - **Hesap bakiyeleri**: yatay çubuklar (8px iz `--color-neutral-300`, dolgu accent-700) + hesap kodu/adı/bakiye.
4. **Son 12 Ay** çizgi grafiği: 34px HTML değer ekseni + `viewBox="0 0 690 200"` SVG (5 kılavuz çizgisi, ciro düz 2px `--color-accent`, tahsilat kesikli 1.5px `--color-accent-600`, ciro altında `--color-accent-300` %28 alan dolgusu, 12 nokta r=2.5), altında 12 ay etiketi HTML satırı. **Not:** SVG `<text>` içine dinamik değer koymayın; DOM'da span'a dönüşüp görünmez oluyor.
5. **Yaşlandırma**: 26px yığın çubuk (accent-300 / accent-600 / accent-800) + yüzdeli lejant + kova tablosu + toplam.
6. **Fatura Durumları** (etiket + adet + tutar + oran çubuğu), **En Çok Alacaklı Cariler**, **Abonelik Hareketi** (+ churn nedenleri), **Yaklaşan Vadeler** tablosu, **Eşleşme Bekleyen** + "Tahsilatlara git".

### Faturalar (faturalar)
- Filtre çubuğu (`flex-wrap:wrap; gap:8px; row-gap:6px`): arama, Durum, Tip, tarih aralığı, "Sadece vadesi geçen" onay kutusu, Temizle, Filtreyi kaydet; sağda sonuç sayacı. Alanlar `flex:none`, düğmeler `white-space:nowrap`.
- Sticky başlık satırı (`--color-neutral-200`), tıklanınca sıralama (▲/▼ accent-700), altında opsiyonel kolon filtre satırı (19px input'lar).
- Satır: 13px onay kutusu, fatura no ghost buton, vadesi geçende kalın vade + `▲45g`, durum etiketi, sağda PDF · XML kısayolu. Odaklı satır accent-100, seçili satır accent %6.
- Seçim yapılınca accent-100 zeminli toplu işlem şeridi (E-Fatura gönder, Tahsilat eşleştir, PDF indir, Muhasebeleştir, İptal et + seçim özeti).
- Sticky tfoot: kayıt sayısı + genel toplam + kalan.
- Altta **kayıt önizleme** paneli: 6 alanlı özet + "Belgeyi aç F4".

### Fatura Editörü (doc)
Başlık + durum etiketleri, 6×2 alan gridi (cari, kod, vergi no, tip, tarih, vade, döviz/kur, ödeme şekli, seri/no, fiş, sorumlu, iskonto), alt sekmeler (Satırlar / Muhasebe Fişi / Tahsilatlar / Belgeler / Geçmiş), satır tablosu (inline düzenlenebilir hücreler: kenarlık hover'da `--color-divider`, focus'ta `--color-accent` + `--color-neutral-100` zemin), "+ Satır ekle CTRL+ENTER", sağda belge toplamları (ara toplam, iskonto, matrah, KDV, tevkifat, **genel toplam accent-100 zeminde Barlow Condensed 17px**, tahsil edilen, kalan). Muhasebe Fişi sekmesi: hesap/açıklama/borç/alacak tablosu + DENGEDE etiketi.

### Liste ekranları (generic grid)
Tek şablon: arama + 1-2 açılır filtre + Temizle/Excel + sonuç sayacı; sticky sıralanabilir başlıklar; zebra satırlar (Ayarlar'dan kapanır); durum kolonları etiket olarak; sticky tfoot toplamları; altta açıklama notu + "Sayfa 1 / 1". Kolon setleri: cariler, urunler, tahsilatlar, planlar, abonelikler, hesap-plani, mizan, fisler, denetim, sistem-testi (dosyadaki `grids` nesnesinde birebir tanımlı).

### Mali tablolar (bilanco, gelir-tablosu)
Başlık + dönem + Excel/PDF/Yazdır; panel(ler) halinde satır türleri: `h` bölüm başlığı (Barlow Condensed 11px uppercase), `r` kalem (22px girinti), `s` ara toplam (kalın), `t` genel toplam (accent-100 zemin, 14px kalın). Bilanço 2 sütun (Aktif | Pasif), Gelir Tablosu tek sütun.

### Rol yetki matrisi (kullanicilar)
Sol 250px: rol listesi (ad + kullanıcı sayısı + açıklama, aktif olan accent-100 + 2px sol çizgi) ve altında roldeki kullanıcılar. Sağ: rol başlığı + kapsam etiketi + Rolü kopyala / Değişiklikleri kaydet + yetki sayacı; 9 satır (modül) × 5 kolon (Görüntüle, Ekle, Değiştir, Sil, Onayla) 14px kare onay düğmeleri (açık: accent-700 dolu + ✓).

### Ayarlar (ayarlar)
Sol panel: 6 anahtar satırı (Koyu tema, Belge sekmeleri, Sol kısayol paneli, Kolon filtre satırı, Fonksiyon tuşu ipuçları, Satır gölgelendirme) — her biri başlık + açıklama + 38×20px switch + AÇIK/KAPALI etiketi; altta "Varsayılana dön". Sağ: Oturum ve Sistem bilgi tabloları + Denetim kaydı / Sistem testi kısayolları. Tercihler tarayıcıda saklanır.

### Profilim (profil)
Kimlik başlığı (44px avatar, ad, e-posta + son giriş, rol etiketi, yetki etiketi), 2 sütunlu kişisel bilgi formu (Ad Soyad, Kullanıcı Kodu [salt okunur], E-posta, Telefon, Unvan*, Departman*, Dil, Saat Dilimi), Kaydet / Parola değiştir; sağda Rol ve Yetki paneli (rol adı, kapsam, açıklama, modül bazında yetki özeti) ve Son Oturumlar tablosu.
\* Unvan ve Departman yalnızca Yönetici rolünde düzenlenebilir; diğer rollerde salt okunur ve "yalnızca sistem yöneticisi değiştirebilir" uyarısı gösterilir.

## Etkileşim ve Davranış
- **Navigasyon**: modül menüsü, sol kısayollar, arama (Enter), satır tıklama ve panel bağlantıları hep `go(id)` üzerinden; `go` sekme yoksa ekler, aktif yapar, menü/arama/bildirim/profil panellerini kapatır ve grid filtrelerini sıfırlar.
- **Klavye**: Ctrl/Cmd+K aramayı odaklar, Esc açık panelleri kapatır, F2 yeni fatura. F4/F5/F9/CTRL+E ipuçları görsel (uygulamada gerçek kısayola bağlanmalı).
- **Bildirim paneli** (380px): başlık + okunmamış sayacı + "Tümünü okundu işaretle"; satırlar modül etiketi, başlık, meta, tür etiketi ve okunmamışlarda 3px accent sol işaret; tıklayınca okundu işaretleyip ilgili ekrana gider; altta "Tüm hareketler · Denetim kaydı".
- **Profil menüsü** (320px): kimlik, oturum rolü değiştirici (Yönetici/Muhasebe/Satış/Okuyucu — rol değişince yetki özetleri, düzenlenebilirlik ve durum çubuğu güncellenir), Profilim / Yetkilerim / Tercihler / Oturumu kapat.
- **Koyu tema**: `[data-theme="dark"]` seçicisiyle token override (aşağıda). Tercih `localStorage['nexus.theme']`.
- Hover: tıklanabilir satır ve menü maddelerinde `--color-accent-100`; koyu kabukta `rgba(242,242,243,.12)`.
- Odak: tasarım sistemi gereği `:focus-visible { outline: 2px solid var(--color-accent); outline-offset: 2px; }`.

## Durum (state)
`active` (ekran id), `tabs[]` (açık sekmeler), `menu` (açık modül), `pq` (arama), `notifOpen`/`notifRead[]`, `profileOpen`, `myRole`, `me{}`, `theme`, `ui{tabs,rail,fkeys,cols,zebra}`, fatura listesi için `q/status/type/overdue/sort/sel/focus`, editör için `lines[]`/`docTab`, grid'ler için `gq/gridFilter/gridSort`, yetki matrisi için `role/permOverrides`.

Veri kaynakları (gerçek uygulamada): DashboardService (KPI, trend, yaşlandırma, top debtors, abonelik hareketi), AccountingReportService (mizan, aylık sonuç, bilanço, gelir tablosu), fatura/tahsilat/abonelik/denetim servisleri.

## Tasarım Token'ları (Industry design system)

### Açık tema
```
--color-bg #f2f2f3   --color-surface #e9e9ea   --color-text #1d1f20
--color-divider color-mix(in srgb, #1d1f20 16%, transparent)
neutral 100→900: #f5f5f8 #e7e7ea #d4d4d7 #b7b7ba #98989b #7a7a7d #5d5d60 #424244 #2b2b2d
accent  100→900: #eef6ff #d6ebff #b5d9fd #94bce3 #749dc4 #597ea3 #416180 #2c455d #1d2d3d
--color-accent #5980a6      --nx-onbar #f2f2f3
```

### Koyu tema (`[data-theme="dark"]`)
```
--color-bg #1b1e21  --color-text #e9eaeb  --color-divider rgba(233,234,235,.20)
neutral 100→900: #212529 #262b30 #333940 #4b525a #6c757e #98a1aa #b3bac1 #ccd2d8 #e9eaeb
accent  100→900: #223140 #2c4255 #3c5b74 #5c87a9 #7ba3c8 #a9cbec #c0daf2 #d6e6f7 #141a20
--color-accent #94bce3      --nx-onbar #e9eaeb      color-scheme: dark
```

### Tipografi
- Başlık: **Barlow Condensed** 600 — ekran başlığı 21px, panel başlığı 17px, KPI 26px, mini KPI 16-17px, bölüm başlığı 11px uppercase .1em.
- Gövde: **Barlow** 400/500/700 — tablo ve form 12px, ikincil 11px, etiket 10px uppercase .08em.
- Sayısal her yerde `font-variant-numeric: tabular-nums`.

### Ölçüler
Kabuk 44 / 32 / 28 / 33 / 23px · sol panel 186px · tablo satırı 3px dikey padding (~22px) · hücre yatay padding 7-9px · buton yükseklikleri 19 / 20 / 21 / 23 / 25px · panel iç boşluk 9-11px · grid boşluğu 10-12px.

### Yüzey kuralları
Köşeler kare (radius 0). Kartlar ve paneller şeffaf, 1px `--color-divider` hairline; öne çıkan paneller `.blueprint` + 4 `<i class="corner tl|tr|bl|br">` köşe işareti. Gölge yalnızca açılır katmanlarda (`--shadow-md/lg`). Tek dolu yüzey: `btn-primary` ve kabuk/durum çubuğu.

## Assetler
Görsel yok. İkonlar **Lucide**, stroke-width 1.5, inline SVG (geri/çan/güneş/ay/çıkış/grid). Fontlar Google Fonts üzerinden Barlow + Barlow Condensed.

## Dosyalar
- `NexusERP UI v2.dc.html` — tüm ekranların bulunduğu prototip (tek dosya; `<x-dc>` şablonu + `class Component` mantığı).
- `styles.css` — Industry tasarım sistemi token ve bileşen sınıfları (`.btn`, `.tag`, `.field`, `.input`, `.blueprint`, `.table`).
- Kaynak uygulama: `src/NexusErp.Web/Components/Layout/{MainLayout,ModuleNav}.razor` ve `Components/Pages/**` — mevcut sayfa/rota isimleri prototipteki ekran id'leriyle eşleşir (`/faturalar`, `/cariler`, `/yaslandirma`, `/mizan`, `/bilanco`, `/gelir-tablosu`, `/fisler`, `/kullanicilar`, `/denetim`, `/sistem-testi`).

## Claude Code için başlangıç promptu

> NexusERP (Blazor Server, .NET, MudBlazor) uygulamasının arayüzünü `design_handoff_nexuserp_ui/README.md` ve `NexusERP UI v2.dc.html` referansına göre yenile.
>
> 1. Önce kabuğu kur: `MainLayout.razor` içinde 44px shell bar (marka → ana sayfa, sayfa başlığı, ortada Ctrl+K sayfa bulucu, dönem çipi, tema düğmesi, bildirim paneli, profil menüsü), `ModuleNav.razor` içinde 32px modül çubuğu + 2 sütunlu mega-menü, altında açık belge sekmeleri şeridi, breadcrumb + sağa hizalı işlem çubuğu ve 23px durum çubuğu.
> 2. Token'ları `styles.css` içinde tut; koyu temayı `[data-theme="dark"]` altında override et ve tercihi localStorage'da sakla (mevcut `nexusTheme` yardımcısıyla).
> 3. Sayfaları README'deki kolon setleri ve düzenlerle güncelle; liste ekranları için tek bir yeniden kullanılabilir "veri gridi" bileşeni yaz (sticky başlık + sıralama + filtre satırı + sticky toplam satırı + zebra).
> 4. Grafikleri README'deki ölçülerle uygula: aylık gelir/gider çubukları + altındaki Gelir/Gider/Net tablosu, 12 aylık ciro/tahsilat çizgisi (alan dolgusu + nokta), yaşlandırma yığın çubuğu. Grafik metinlerini SVG `<text>` yerine HTML katmanında render et.
> 5. Ayarlar ve Profilim ekranlarını ekle; yetki kontrolünü `AuthorizeView`/rol talepleriyle bağla (Unvan/Departman ve rol alanları yalnızca Admin).
> 6. Tüm veriler gerçek servislerden gelsin; prototipteki rakamlar örnektir. Yoğunluk ölçülerini büyütme.
