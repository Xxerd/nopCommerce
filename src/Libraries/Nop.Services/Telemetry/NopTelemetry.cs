using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nop.Services.Telemetry;

/// <summary>
/// Central telemetry definitions for nopCommerce observability.
///
/// ActivitySource and Meter live here so every component that wants
/// to emit spans or metrics references the same name/version strings —
/// essential for the OTel SDK to wire them up correctly.
/// </summary>
public static class NopTelemetry
{
    public const string ServiceName = "nopcommerce";
    public const string Version = "1.0.0";
    public const string CatalogMeterName = ServiceName + ".catalog";

    // ── Tracing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ActivitySource for the admin product-publish flow.
    /// Covers: admin HTTP request → product service → database → cache invalidation.
    /// </summary>
    public static readonly ActivitySource CatalogSource =
        new(ServiceName + ".catalog", Version);

    // ── Metrics ──────────────────────────────────────────────────────────────

    private static readonly Meter _catalogMeter = new(CatalogMeterName, Version);

    /// <summary>
    /// Duration of ProductService.InsertProductAsync / UpdateProductAsync.
    ///
    /// Operational rationale: isolates the database write from total HTTP latency.
    /// If p95 spikes while HTTP p95 also spikes → the DB is the bottleneck.
    /// If HTTP p95 spikes but this stays flat → the problem is elsewhere
    /// (middleware, model binding, slug generation, cache clear).
    /// Tag "action": create | update.
    /// Tag "product.type": SimpleProduct | GroupedProduct.
    /// </summary>
    public static readonly Histogram<double> ProductSaveDuration =
        _catalogMeter.CreateHistogram<double>(
            "nopcommerce.catalog.product.save_duration",
            unit: "ms",
            description: "Duration of the DB write for a product save (insert or update), isolated from HTTP overhead");

    /// <summary>
    /// Duration of ProductCacheEventConsumer.ClearCacheAsync.
    ///
    /// Operational rationale: isolates cache pressure from DB pressure.
    /// A spike here (with a flat save_duration) means the cache layer is
    /// saturated — the next wave of storefront requests will all miss cache
    /// and hit the DB simultaneously (thundering herd).
    /// Tag "event_type": Insert | Update | Delete.
    /// </summary>
    public static readonly Histogram<double> CacheClearDuration =
        _catalogMeter.CreateHistogram<double>(
            "nopcommerce.catalog.cache.clear_duration",
            unit: "ms",
            description: "Duration of cache invalidation triggered by a product save — spikes signal cache pressure and thundering-herd risk");

    /// <summary>
    /// Count of product form submissions rejected by server-side validation.
    ///
    /// Operational rationale: nopCommerce returns HTTP 200 with inline form
    /// errors when ModelState is invalid — these failures are invisible to
    /// standard HTTP error-rate metrics. A spike here means admins are hitting
    /// a broken or changed validation rule without any server-side 4xx/5xx signal.
    /// Tag "action": create | update.
    /// </summary>
    public static readonly Counter<long> ProductValidationFailures =
        _catalogMeter.CreateCounter<long>(
            "nopcommerce.catalog.product.validation_failures",
            unit: "{failures}",
            description: "Server-side validation rejections for product create/update — invisible to HTTP metrics because they return 200 OK");
}
