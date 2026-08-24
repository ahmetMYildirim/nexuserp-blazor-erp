# NexusERP

**Multi-tenant accounting and subscription billing system.**

.NET 9 · Blazor Server · EF Core 9 · PostgreSQL 17 · RabbitMQ · Clean Architecture

Sales and purchase invoices, payment allocation, customer/supplier ledgers,
**double-entry bookkeeping** (Turkish Uniform Chart of Accounts, journal entries, trial
balance, balance sheet, income statement), subscription/MRR management, usage-based
billing, dunning, event publishing via the outbox pattern, e-Invoice (UBL-TR 1.2) and
role-based user management.

> The application UI is in Turkish. Menu paths in this document keep their Turkish
> labels with an English translation in parentheses, so you can follow along in the app.

---

## Contents

- [Screenshots](#screenshots)
- [Setup](#setup)
- [Demo accounts](#demo-accounts)
- [Verifying the functionality](#verifying-the-functionality)
- [What's included](#whats-included)
- [Architecture](#architecture)
- [Technically notable parts](#technically-notable-parts)
- [Tests](#tests)
- [REST API](#rest-api)
- [Performance](#performance)
- [Out of scope](#out-of-scope)
- [License](#license)

---

## Screenshots

### Overview

Open receivables, overdue amounts, MRR and monthly revenue; a 12-month revenue/collection
trend and the aging distribution. **Supplier payables** and **net position** are shown
separately — a purchase invoice is a payable, not a receivable, and must not leak into
the "open receivables" card.

![Overview](docs/screenshots/02-genel-bakis.png)

### Live system test

All 37 checks run against a real database. Each row shows the result, the **concrete
output**, and why the rule exists in the first place.

![System test — summary and infrastructure](docs/screenshots/12-sistem-testi.png)

Subscription and usage-based billing rules, with real invoice numbers and amounts:

![System test — subscriptions and usage](docs/screenshots/13-sistem-testi-abonelik.png)

These two frames alone prove: the anchor day does not drift
(`31 Jan → 28 Feb → 31 Mar`) · proration `499.00 × 17/31 = 273.65` · the same period is
never billed twice (`invoice count 1 → 1`) · quota overage becomes its own line
(`flat 500.00 + usage 200 × 2.00 = taxable base 900.00`) · all billed usage is stamped ·
late-arriving usage records are not lost.

### Purchase invoice

Selecting type "Alış" (Purchase) switches the party lookup to **suppliers** and makes the
**supplier invoice number** field required. The number is not drawn from our own series —
it comes from the supplier's document, so `Sequence` does not advance.

![Purchase invoice](docs/screenshots/05-alis-faturasi.png)

### Subscription plans

Flat-rate, **flat + usage** (hybrid) and **pure usage** plans side by side. A pure usage
plan contributes `0.00 ₺` to MRR — there is no committed recurring revenue, the amount
changes with consumption every month.

![Plans](docs/screenshots/08-planlar.png)

### User and permission management

Role assignment, account status, last login and password reset. Users are never deleted,
only deactivated: audit records and the "created by / modified by" fields on documents
still refer to them.

![Users](docs/screenshots/09-kullanicilar.png)

### Other screens

| | |
|---|---|
| ![Invoices](docs/screenshots/03-faturalar.png) | ![Invoice editor](docs/screenshots/04-fatura-editoru.png) |
| **Invoice list** — status, type and due-date filters | **Invoice editor** — live VAT/withholding preview |
| ![Subscriptions](docs/screenshots/07-abonelikler.png) | ![Party statement](docs/screenshots/06-cari-ekstre.png) |
| **Subscriptions** — MRR, renewal calendar, bulk billing | **Party statement** — transactions with a running balance |
| ![Aging](docs/screenshots/11-yaslandirma.png) | ![Audit log](docs/screenshots/10-denetim.png) |
| **Aging report** — 30/60/90 day buckets | **Audit log** — who, when, which field, from what to what |
| ![Login](docs/screenshots/01-giris.png) | |
| **Login** — demo accounts listed on screen | |

---

## Setup

### Requirements

| Tool | Version | Note |
|---|---|---|
| .NET SDK | 9.0+ | `dotnet --version` |
| Docker Desktop | current | For PostgreSQL, RabbitMQ and MailHog |
| RAM | ~4 GB free | Four containers plus the app |

You do **not** need to install a database, edit a connection string or run migrations —
all of it is automatic.

### 1. Start the infrastructure

```bash
docker compose up -d
```

Four containers come up:

| Service | Container | Port | UI |
|---|---|---|---|
| PostgreSQL 17 | `nexuserp-db` | **5433** | — |
| pgAdmin | `nexuserp-pgadmin` | 5050 | http://localhost:5050 |
| RabbitMQ | `nexuserp-mq` | **5673** / 15673 | http://localhost:15673 |
| MailHog (fake SMTP) | `nexuserp-mail` | 1025 / 8025 | http://localhost:8025 |

> **Why non-standard ports?** PostgreSQL on 5433, RabbitMQ on 5673. If you already have a
> local PostgreSQL or RabbitMQ installed, the standard ports are taken and the app will
> silently connect to the **wrong server**, producing an `ACCESS_REFUSED` error that is
> painful to diagnose. This happened while building the project.

Wait for PostgreSQL to become ready (~10 s on first start):

```bash
docker compose ps
```

Wait until the `nexuserp-db` row reports `(healthy)`.

### 2. Run the application

```bash
dotnet run --project src/NexusErp.Web
```

→ **http://localhost:5283**

On first start, in order:

1. Migrations are applied (schema created from scratch)
2. Demo data is generated: 5 parties, 4 products, 6 subscription plans, invoices,
   payments, usage records
3. Four demo users and the roles are created
4. Background workers start (subscription billing, outbox publisher, dunning,
   notification consumer, outbox cleanup)

It is ready once the console prints `Now listening on: http://localhost:5283`.

### 3. (Optional) REST API

```bash
dotnet run --project src/NexusErp.Api
```

→ http://localhost:5299/scalar/v1 — interactive documentation.

### Running everything in containers

```bash
docker compose --profile full up -d --build
```

→ http://localhost:8080

### Shutting down

```bash
docker compose down          # stop containers, KEEP data
docker compose down -v       # also delete data (to start from scratch)
```

### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Npgsql...Connection refused` | PostgreSQL is not ready yet. Wait for `(healthy)` in `docker compose ps`. |
| `ACCESS_REFUSED` (RabbitMQ) | A local RabbitMQ may be holding port 5672. Compose uses 5673; the URI in `appsettings.Development.json` must **not end with `/`** — a trailing slash makes the vhost an empty string rather than `/`. |
| Port 5283 in use | `dotnet run --project src/NexusErp.Web --urls http://localhost:5400` |
| Turkish characters sort incorrectly | PostgreSQL must be created with the ICU locale. Drop the volume with `docker compose down -v` and recreate. |
| No e-mail arrives | MailHog never actually sends mail; it displays messages at http://localhost:8025. |
| Schema error (`column ... does not exist`) | Recreate from scratch: `docker compose down -v && docker compose up -d`. |

---

## Demo accounts

Password for all of them: **`Demo!2026`**

| E-mail | Role | Permissions |
|---|---|---|
| `admin@nexusdemo.com.tr` | Admin | Everything + user management + system test |
| `muhasebe@nexusdemo.com.tr` | Accounting | Issuing invoices, payments, subscriptions, reports |
| `satis@nexusdemo.com.tr` | Sales | Creating parties and invoices — **no payments** |
| `bakis@nexusdemo.com.tr` | Viewer | Read-only |

To see the role separation, sign in as `satis@` and try to issue an invoice: the button is
hidden, and going straight to the URL returns **403**. Permissions are not merely hidden in
the UI, they are enforced on the server.

> **Note:** These accounts and the demo data are only seeded in the `Development`
> environment. So that an admin account whose password is published here never reaches
> production, seeding does not run in other environments; enable it deliberately with
> `Seed:DemoData=true` if you need it. Likewise the JWT signing key in
> `appsettings.Development.json` is rejected outside development — supply a real key via
> the `Jwt__Key` environment variable.

---

## Verifying the functionality

### A. Live system test (fastest route)

Sign in as `admin@` → **Sistem → Sistem Testi** (System → System Test) → *Testleri Çalıştır*
(Run Tests).

**37 checks** run against real services and a real database, and each reports three things:
the result, the **concrete output** (actual numbers and amounts), and why that rule exists.
On the reference machine a full run takes **7.9 seconds** and passes 37/37
([screenshot](#live-system-test)).

> The checks run in a **separate tenant** and are deleted when the run ends. The reason:
> issuing an invoice consumes a number; running in the demo company would leave gaps in a
> series that gets reported to the tax authority. Your demo data and number series are
> untouched.

Areas covered:

| Area | Verified behaviour |
|---|---|
| Infrastructure | PostgreSQL, schema currency, outbox health, RabbitMQ, SMTP |
| Parties & sales invoices | Tax-number checksum · discount → VAT → withholding ordering · gap-free number series · immutability of an issued invoice · ledger direction |
| Purchase invoices | Number comes from the supplier, our own series is untouched · ledger direction is the inverse of sales · duplicate supplier invoice rejected |
| Payments | FIFO allocation · invoice status · party balance |
| Subscriptions | Anchor day does not drift · prorated amounts · billing preview · a period is never billed twice · dunning and recovery |
| Usage-based | Quota overage · source-number idempotency · stamping · late-arriving records |
| Accounting | Chart of accounts · automatic journal entries from invoices and payments · unbalanced entry rejected · duplicate entry blocked · trial balance balances · balance sheet assets = liabilities · income statement consistency |
| Messaging | Writing to the outbox and publishing to the broker |
| Users & permissions | Tenant isolation · role enforcement · audit logging |

### B. Manual demo walkthrough

If you want to watch, step by step in the UI, what the system test automates:

**1 · Issue an invoice and watch numbers being assigned in sequence**
`Satış → Yeni Fatura` (Sales → New Invoice) → pick a party, add a line → *Taslağı Kaydet*
(Save Draft). There is **no** invoice number yet (it is a draft). *Faturayı Kes* (Issue
Invoice) → a `NEX2026...` number is assigned. Issue another one: the number increments by
one, with no gaps.

**2 · An issued invoice cannot be changed**
Open the invoice you issued: the fields are read-only and a "Kesildi" (Issued) badge is
shown.

**3 · Purchase invoice — the number comes from the supplier**
`Faturalar → Alış Faturası Gir` (Invoices → Enter Purchase Invoice). The party lookup now
searches **suppliers**. Type `TED-001` into the "Tedarikçi Fatura No" (Supplier Invoice No)
field and save. The invoice number is exactly what you typed — your own series is not
consumed. Try entering the same number for the same supplier a second time: it is
**rejected**. Open `Cari → Cari Ekstre` (Parties → Party Statement) and select the
supplier: the entry is on the **credit** side (debit for sales, credit for purchases).

**4 · Payments are allocated FIFO**
`Finans → Tahsilatlar → Yeni Tahsilat` (Finance → Payments → New Payment) → pick a party,
enter an amount, leave *auto-allocate* on. The earliest-due invoice is settled first and
its status flips to "Tahsil Edildi" (Paid).

**5 · Subscription billing and preview**
`Abonelik → Abonelikler → Şimdi Faturalandır` (Subscriptions → Subscriptions → Bill Now).
A **preview** opens first: which party, which period, how much. Confirm it. Press the same
button again: a second invoice is **not** produced ("will be skipped, already billed").

**6 · Usage-based billing**
On `Abonelik → Planlar` (Subscriptions → Plans), find **SMS Paketi** (flat + usage) and
**API Kullanımı** (pure usage). Open a subscription bound to those plans → the **Kullanım**
(Usage) panel shows period usage, remaining free quota and the estimated amount. Add usage,
then bill: the overage enters the invoice as a **separate line**. On the invoice, the flat
line covers the *upcoming* period while the usage line covers the *past* one — a period's
usage can only be known once the period has ended.

**7 · Dunning**
A party with an overdue subscription invoice moves to `Gecikmiş` (Overdue) status.
Reminder e-mails go out on days 3/7/14, and suspension happens on day 21. Read the mail at
http://localhost:8025 (MailHog). Once the debt clears, the subscription returns to normal
automatically.

**8 · Outbox → RabbitMQ → e-mail chain**
After issuing an invoice:
- http://localhost:15673 (`nexus` / `nexus_dev_2026`) → the message is in the queue
- http://localhost:8025 → the e-mail sent to the customer
- http://localhost:5283/saglik → outbox health status (JSON)

Stop RabbitMQ (`docker stop nexuserp-mq`) and issue an invoice: the invoice **is issued**,
the message waits in the outbox and `attempt_count` increases. Bring the broker back: the
message is published. Nothing is lost.

**9 · Double-entry bookkeeping — journal, trial balance, balance sheet**
`Muhasebe → Muhasebe Fişleri` (Accounting → Journal Entries): an **automatic entry** exists
for every invoice you issued and every payment you processed. Open a sales invoice's entry:
`120 Alıcılar` (Receivables) debit / `600 Yurtiçi Satışlar` (Domestic Sales) +
`391 Hesaplanan KDV` (Output VAT) credit. On a purchase invoice the direction is reversed:
`153`+`191` debit / `320 Satıcılar` (Payables) credit.

Enter a manual journal with `Muhasebe → Yeni Fiş` (Accounting → New Entry) — e.g.
`770 Genel Yönetim Gideri` (General Admin Expense) debit / `100 Kasa` (Cash) credit. While
debits and credits do not match, the **Kesinleştir** (Post) button stays disabled; if you
force it, the server rejects it too — an unbalanced entry cannot be written to the database
(CHECK constraint).

`Muhasebe → Mizan` (Trial Balance): total debit = total credit, confirmed by a green badge
at the top. `Muhasebe → Bilanço` (Balance Sheet): assets = liabilities (with the period
result included in liabilities). `Muhasebe → Gelir Tablosu` (Income Statement):
revenue − expense = net result for the period, matching the balance sheet exactly.

**10 · User and permission management**
`Sistem → Kullanıcılar` (System → Users), Admin only. Create a user — the password is
generated automatically and shown **once**. Change roles, deactivate accounts. Try to
deactivate your own account: blocked. Try to demote the last administrator: blocked.

**11 · Audit log**
`Sistem → Denetim Kaydı` (System → Audit Log): every change you made is listed as
who / when / which field / old value / new value.

**12 · Document output**
From the row menu in the invoice list, export an invoice as **PDF**, **UBL-TR XML** or
**Excel**.

---

## What's included

**Accounting operations.** Party records (customer/supplier, with Turkish tax-number
validation), product/service catalogue, VAT rates, sales and **purchase** invoices, credit
notes, proformas, payments with FIFO allocation, party ledgers, 30/60/90 day aging report.

**Double-entry bookkeeping.** Turkish Uniform Chart of Accounts (hierarchical, businesses
can open their own sub-accounts), journal entries (draft/posted, an unbalanced entry cannot
be posted), **automatic entries** generated from sales and purchase invoices and from
payments (in the same transaction as the document, with duplicates blocked at the database
level), withholding-tax support, trial balance, balance sheet and income statement.

**Subscriptions and billing.** Plans (monthly/quarterly/semi-annual/annual), trial periods,
prorated plan changes, pause/resume/cancel, cancellation reasons and churn analysis,
MRR/ARR, automatic periodic billing, bulk billing preview, dunning.

**Usage-based billing.** Flat / usage-based / hybrid plans, free quota, overage pricing,
event-based usage records, an idempotent REST endpoint for integrations.

**Infrastructure.** Outbox pattern + RabbitMQ, at-least-once delivery, consumer-side
idempotency ledger, DLQ, e-mail notifications, audit log, health endpoint, background
workers.

**Documents.** Invoice PDF (QuestPDF), UBL-TR 1.2 e-Invoice XML, Excel export with formulas.

---

## Architecture

```
NexusErp.Web (Blazor) ─┐
                       ├─→ Infrastructure ─→ Application ─→ Domain
NexusErp.Api (REST)  ──┘      EF Core          services       business rules
                              PostgreSQL       DTOs           NO NuGet dependencies
                              RabbitMQ
```

Dependencies point one way: outside in. Web and API use the **same** Application services;
the only difference between them is the identity source (cookie vs JWT).

| Decision | Rationale |
|---|---|
| No MediatR | v13+ is commercially licensed; plain services keep stack traces readable |
| No repository layer | `DbSet<T>` is already a repository, `DbContext` is already a Unit of Work |
| `decimal(18,4)` + `AwayFromZero` | .NET defaults to banker's rounding, which costs cents on invoices |
| Soft delete + partial unique index | Accounting data is never deleted; a deleted row must not occupy the index |
| `IDbContextFactory` | In Blazor a scoped service lives for the whole circuit; every operation opens a fresh context |
| Outbox + RabbitMQ | With direct publishing, a delivery failure would leave the invoice issued but nobody notified |

---

## Technically notable parts

**VAT / withholding / discount engine.** Line-level rounding (per tax-authority rules) and
proportional document-discount distribution with no lost cents: splitting 100 TL across
three lines yields exactly `100.00`, not `33.33 × 3 = 99.99`. The engine is a pure function
in Domain, so the live preview in the UI and the record written on the server call the
**same code** — the two can never diverge.

**Atomic invoice numbering.** A single `INSERT ... ON CONFLICT ... RETURNING` statement.
Across 50 parallel requests the series stays collision-free and gap-free — proven by test.
`SELECT MAX(no)+1` is open to a race condition.

**Purchase invoices generate no number.** The number comes from the supplier's document; if
we assigned one from our own series we would open a gap in the sales series reported to the
tax authority. Duplicate entry is blocked by a `(tenant, party, supplier_invoice_no)` unique
index — this is the single most common manual-entry mistake, and it inflates both the ledger
and expenses.

**Idempotent subscription billing.** A second invoice is never produced for the same
subscription and period. The guarantee lives in the `(subscription_id, period_start)` unique
index rather than in business logic: under a race the application layer can be wrong, the
database constraint cannot.

**The "last day of month" problem.** A monthly subscription starting 31 January runs
31 Jan → 28 Feb → **31 Mar** (not 28 Mar). The anchor day is stored separately;
`AddMonths` alone would shift the day permanently.

**Usage: billed by stamp, not by date.** If billable usage were selected as "records in this
period", usage that an integration sent a day late would never make it onto any invoice.
Records are stamped with the invoice (`invoice_id`), and the stamp is the single source of
truth. Records that stayed inside the quota and were not charged are stamped too — otherwise
they would consume the quota a second time on the next run.

**Flat fee in advance, usage in arrears.** A hybrid invoice contains two lines covering two
different periods; this is not a bug but a necessity — a period's usage can only be known
once the period has ended.

**Outbox pattern.** The event is written in the **same transaction** as the invoice: either
both happen or neither does. The publisher uses `FOR UPDATE SKIP LOCKED`, so multiple
instances run safely. Delivery is at-least-once; on the consumer side an idempotency ledger
with a `(consumer, message_id)` unique index prevents the same message being processed
twice.

**Tenant isolation.** An EF Core global query filter is applied centrally, via reflection, to
every `ITenantScoped` entity. **Exception:** ASP.NET Identity users do not implement that
interface, so the filter does not reach them — every query in user management adds the tenant
filter explicitly, and that behaviour is separately tested.

**Audit log.** Every change is written as JSON through `SaveChanges`: who, when, which field,
from what value to what value.

**e-Invoice (UBL-TR 1.2).** XML in the format required by the Turkish tax authority:
`CustomizationID=TR1.2`, ETTN, a separate `TaxSubtotal` per VAT rate,
`WithholdingTaxTotal` for withholding, UN/ECE Rec.20 unit codes. The integrator connection
sits behind `IEInvoiceGateway`.

---

## Tests

```bash
dotnet test
```

**234 tests** (one performance benchmark is skipped by default). The calculation engine is
covered by pure unit tests (milliseconds); services run against a real PostgreSQL via
**Testcontainers**. The InMemory provider was deliberately not used — it cannot reproduce
partial indexes, ICU collation or precision behaviour, so tests would pass and production
would break.

> Tests require Docker; Testcontainers starts a throwaway PostgreSQL for each run.

Highlights: no number collisions across 50 parallel requests · one tenant cannot see
another's data · no second subscription invoice for the same period · no lost cents in
document discounts · the same usage is never billed twice · the last administrator cannot be
demoted.

The system-test screen itself is tested too: a screen that shows "all green" during a demo
**lies** if one of its checks silently stops verifying anything — false confidence is worse
than no tests at all.

---

## REST API

```bash
dotnet run --project src/NexusErp.Api
```

→ http://localhost:5299/scalar/v1

JWT authentication, role-based authorization, rate limiting. Added **without touching** the
Application layer. Permissions apply to the API as well: with the Sales role,
`POST /api/faturalar/{id}/kes` → **403**.

Main endpoints: `/api/auth/token` · `/api/cariler` (parties) · `/api/faturalar` (invoices;
plus `/kes` issue, `/pdf`, `/ubl`) · `/api/tahsilatlar` (payments; plus `/yaslandirma`
aging) · `/api/abonelikler/faturalandir` (bill subscriptions, idempotent) ·
`/api/abonelikler/{id}/kullanim` (usage record — idempotent when `kaynakNo` is supplied, so
an integration retry never bills the customer twice).

---

## Performance

With 100,000 invoices across 500 parties: the aging report takes **80 ms**
(`Seq Scan` + `HashAggregate`), and a single party's open invoices **2 ms**
(`Bitmap Index Scan`).

The hypothesis was that a covering index would speed up the aging report; measurement showed
no meaningful difference. The query has no selective filter and reads the whole table — an
index does not speed that up, it only adds write cost. **Decision: no index added.**

```bash
dotnet test --filter FullyQualifiedName~AgingReportBenchmark
```

---

## Out of scope

Inventory/warehouse management, cash and bank account tracking with statement
reconciliation, cheques and promissory notes, fixed assets and depreciation, multi-currency
and exchange-rate differences, VAT returns, period-end closing, a real integrator connection
(UBL-TR XML generation is ready, the connection is not), a plan editor screen (plans arrive
with the seed data).

The goal was depth, not breadth: the billing and subscription engine is written to
production quality.

---

## License

This software is **proprietary and all rights are reserved**. The source being publicly
visible grants no usage rights whatsoever.

Copying, modification, distribution, commercial use, sublicensing, reverse engineering and
use as AI training data are **expressly prohibited**.

See the [LICENSE](LICENSE) file for details.

© 2026 Ahmet Yıldırım. All rights reserved.
