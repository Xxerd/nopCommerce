# nopCommerce — Observability Assignment

## Architecture

### Layer organisation

nopCommerce follows a strict dependency hierarchy across three library projects:

- **`Nop.Core`** — domain entities (`Product`, `Customer`, `Order`, …) and shared abstractions. No business logic, no I/O. Nothing depends on this layer except everything above it.
- **`Nop.Services`** — all business logic (`ProductService`, `CustomerService`, …), repository access, caching, and event publishing. Depends only on `Nop.Core`.
- **`Nop.Web`** — ASP.NET Core presentation layer: controllers, views, and API endpoints. Depends on `Nop.Services`. All HTTP entry points live here.

This layering means that an `InsertProductAsync` call in `ProductService` is the single authoritative place where a product enters the database. Instrumenting that one method covers every call site — whether triggered by the admin UI, an API, or an import job.

### The IEventPublisher pattern

Rather than calling cache-clearing or notification code directly, every service that mutates an entity delegates to `IEventPublisher`. After `EntityRepository.InsertAsync` (or `UpdateAsync`, `DeleteAsync`) completes the database write, it calls:

```csharp
await _eventPublisher.EntityInsertedAsync(entity);
```

The framework then resolves all registered `IConsumer<EntityInserted<T>>` implementations and invokes them in sequence. For `Product`, one of those consumers is `ProductCacheEventConsumer`, which clears the relevant cache key groups.

This pattern decouples cache management from service logic entirely. It also creates a natural instrumentation boundary: a single override of `ClearCacheAsync` in `ProductCacheEventConsumer` intercepts every product mutation regardless of origin.

### Where observability is easy — and where it is not

The layered architecture and the event bus make the business-logic layer straightforward to instrument. `ProductService` is the DB boundary; `ProductCacheEventConsumer` is the cache boundary. Both are small, focused classes.

What is harder is everything the framework does invisibly. There is no telemetry infrastructure in nopCommerce out of the box — no `ActivitySource`, no `Meter`, no structured logging conventions. Middleware (routing, authentication, anti-forgery validation, model binding) accounts for roughly half of end-to-end HTTP latency but is opaque to application code. And because form validation failures return HTTP 200 with inline errors, the entire standard monitoring stack produces no signal for that failure class.

---

## Instrumented Flow — Admin Publishes a New Product

The chosen flow covers the full write path: HTTP entry → controller → DB write → cache invalidation.

```
Browser POST /Admin/Product/Create
│
│
└─ catalog.product.create                              Added span
      ├─ Npgsql INSERT Product                         Automatic
      │
      ├─ catalog.cache.clear                           Added span
      │
      ├─ SaveDiscountMappingsAsync
      │    └─ Npgsql UPDATE Product                    Automatic
      │
      └─ catalog.cache.clear                           Added span
```
Tags:
Catalog.product.create/update: product.id, product.name, product.published, product.type, admin.id
Catalog.cache.clear: event_type (Insert/Update/Delete), cache.keys_cleared, product.id


---

## Custom Metrics

### `nopcommerce_catalog_product_save_duration_milliseconds` (histogram)

Measures the time spent exclusively inside `InsertProductAsync` or `UpdateProductAsync` — the DB write in isolation, stripped of HTTP overhead, middleware, and post-save work.

**Tags:** `action` (`create` / `update`), `product.type` (`SimpleProduct` / `GroupedProduct`).

---

### `nopcommerce_catalog_cache_clear_duration_milliseconds` (histogram)

Measures the time spent inside `ProductCacheEventConsumer.ClearCacheAsync` after each product mutation. This isolates the cache layer from the DB layer: if HTTP p95 spikes while DB save duration stays flat, the bottleneck is here.

**Tags:** `event_type` (`Insert` / `Update` / `Delete`).

---

### `nopcommerce_catalog_product_validation_failures_total` (counter)

Counts server-side model validation rejections on the product create/edit form.

**Why this metric exists:** nopCommerce returns HTTP 200 when validation fails — the form re-renders with inline error messages. From the perspective of any external monitor, uptime check, or infrastructure alert based on HTTP status codes, the request succeeded. This counter is the only signal that something is wrong. The Grafana dashboard makes this gap explicit: when the validation failures line rises while the HTTP error rate line stays at zero, the form is broken but no alarm fires.


---

## Privacy Strategy

The privacy strategy follows a *privacy by design* approach across two layers — sensitive data is excluded before it enters the pipeline, with the collector acting as a defensive fallback.

**SDK layer (C# code) — exclusion by omission**

Span tags contain only integer IDs and enums: `product.id`, `product.type`, `event_type`, `cache.keys_cleared`, `admin.id`. No string values that could carry personal data are emitted. Specifically:
- The identity of the authenticated admin is captured as `admin.id` (integer) only — never `admin.email`, `admin.username`, or any other `Customer` field.
- HTTP headers (`Cookie`, `Authorization`) are not captured — `AddAspNetCoreInstrumentation()` uses the route template, not the raw URL or headers.
- Request bodies and form field values are never added to any span.
- The one discutible field is `product.name`, which is a free-text string and could contain sensitive business data in a production context; in production it would be removed in favour of `product.id`-only correlation.

**Collector layer — defensive redaction**

The OTel Collector applies a second layer of protection for fields that third-party libraries or future code changes might emit. The `attributes/redact_pii` processor deletes the following attributes before any data reaches Jaeger or Prometheus:

| Attribute | Reason |
|---|---|
| `admin.email`, `admin.username` | Direct identifiers from `Customer` entity |
| `admin.first_name`, `admin.last_name` | Personal data from `Customer` entity |
| `admin.phone`, `admin.ip_address` | Personal data from `Customer` entity |
| `http.request.header.cookie` | Session token |
| `db.connection_string` | Exposes DB username and host topology (emitted by Npgsql automatically) |
| `db.user` | Exposes DB username (emitted by Npgsql automatically) |
| `net.peer.ip` | Exposes internal infrastructure IP (emitted by Npgsql automatically) |
| `db.statement` | Hashed — preserves query identity for debugging without exposing literal values |

Note: `db.statement` is already hashed by Npgsql before emission; the collector rule is a redundant safeguard.

---

## Grafana Dashboard

![nopCommerce Grafana dashboard](./images/grafana.png)


## Jaegar Trace

![nopCommerce Jaeger trace](./images/jaeger.png)

## Load Tests

Two scripts are provided under `loadtest/`. Both handle ASP.NET Core anti-forgery tokens (CSRF) by extracting `__RequestVerificationToken` from the form HTML on each iteration.

### `k6-admin-product-publish.js` — steady throughput baseline

A simple three-stage ramp designed to verify that the instrumentation pipeline is working end to end and that all dashboard panels show data.

| Stage | Duration | VUs |
|---|---|---|
| Ramp up | 30 s | 0 → 5 |
| Steady | 2 min | 5 |
| Ramp down | 30 s | 5 → 0 |

Expected throughput: ~1–2 product creates/s. Thresholds: `http_req_failed < 10%`, `p(95) < 10 s`, at least 10 products created.

```bash
k6 run loadtest/k6-admin-product-publish.js
```

---

### `k6-demo-story.js` — four-phase observability story

Designed for a live dashboard demo. Each phase produces a distinct, readable signal in Grafana.

| Phase | Time | What happens | What the dashboard shows |
|---|---|---|---|
| 1 — Baseline | 0 → 1 min | 1 req/s | Stable publish rate, DB p95 low, cache clear fast |
| 2 — Rate spike | 1 min → 2m30s | Ramps to 8 req/s | Publish rate jumps 8×; system absorbs it without latency degradation |
| 3 — Recovery | 2m30s → 3m30s | Back to 1 req/s | All metrics return to baseline |
| 4 — Silent failures | 3m30s → 4m30s | 2 VUs submit forms without `Name` | `validation_failures` counter rises; HTTP error rate stays 0% |

```bash
k6 run loadtest/k6-demo-story.js
```

---

## How to Run

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Docker](https://docs.docker.com/get-docker/) + Docker Compose
- [k6](https://k6.io/docs/getting-started/installation/) (for the load tests)

---

### 1. Start the observability stack

```bash
docker compose -f docker-compose.observability.yml up -d
```

| Service | URL |
|---|---|
| OTel Collector (OTLP gRPC) | `localhost:4317` |
| Jaeger UI | http://localhost:16686 |
| Prometheus | http://localhost:9090 |
| Grafana | http://localhost:3000 (admin / admin) |

The Grafana dashboard **nopCommerce — Admin Product Publish Flow** is provisioned automatically on startup.

---

### 2. Database (PostgreSQL)

Either use a local PostgreSQL installation or Docker:

**Local PostgreSQL** — just ensure the DB exists:
```bash
psql -U postgres -c "CREATE DATABASE nopcommerce;"
```

**Docker** (alternative):
```bash
docker run --name nopcommerce_postgres \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=nopcommerce \
  -p 5432:5432 -d postgres:15
```

---

### 3. Run nopCommerce

```bash
cd src/Presentation/Nop.Web
OpenTelemetry__OtlpEndpoint=http://localhost:4317 dotnet run
```

On first run the app redirects to the install wizard at http://localhost:5000/install.
Use the connection string: `Host=localhost;Database=nopcommerce;Username=postgres;Password=postgres`

Default admin credentials after install:
- **Email:** andre@gmail.com
- **Password:** Admin@123

---

### 4. View traces and metrics

- **Grafana dashboard:** http://localhost:3000 → Dashboards → nopCommerce → *Admin Product Publish Flow*
- **Jaeger traces:** http://localhost:16686 → Service: `nopcommerce` → Operation: `catalog.product.create`
