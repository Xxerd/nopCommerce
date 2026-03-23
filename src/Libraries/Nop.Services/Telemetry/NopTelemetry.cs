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
    /// Counts products published by admins.
    ///
    /// Operational rationale: a sudden drop to zero during business hours signals
    /// a broken admin workflow (permission issue, DB error) before any user report
    /// reaches the team.  Alert threshold: 0 publishes over a 30-minute window
    /// during catalogue-update periods.
    /// </summary>
    public static readonly Counter<long> ProductsPublished =
        _catalogMeter.CreateCounter<long>(
            "nopcommerce.catalog.products_published",
            unit: "{products}",
            description: "Number of products published by admins");

    /// <summary>
    /// Counts cache keys cleared per product publish operation.
    ///
    /// Operational rationale: an abnormal spike reveals that a single product
    /// update is evicting an unexpectedly large slice of the cache, which can
    /// cause a thundering-herd DB load spike immediately after.
    /// Pair with a DB query-rate panel to correlate cache misses → DB pressure.
    /// </summary>
    public static readonly Counter<long> CatalogCacheKeysCleared =
        _catalogMeter.CreateCounter<long>(
            "nopcommerce.catalog.cache_keys_cleared",
            unit: "{keys}",
            description: "Number of cache keys cleared as a result of a product publish/update");
}
