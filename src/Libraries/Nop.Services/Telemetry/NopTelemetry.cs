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
    public const string OrderMeterName = ServiceName + ".orders";

    // ── Tracing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ActivitySource for the order-placement flow.
    /// Covers: checkout validation → payment processing → order persistence.
    /// </summary>
    public static readonly ActivitySource OrderSource =
        new(ServiceName + ".orders", Version);

    // ── Metrics ──────────────────────────────────────────────────────────────

    private static readonly Meter _orderMeter = new(OrderMeterName, Version);

    /// <summary>
    /// Counts orders successfully placed.
    ///
    /// Operational rationale: a sudden drop below the rolling baseline at any
    /// hour is the first signal of a broken checkout pipeline — before support
    /// tickets arrive.  Alert threshold: &lt;50 % of p50 over a 5-minute window.
    /// </summary>
    public static readonly Counter<long> OrdersPlaced =
        _orderMeter.CreateCounter<long>(
            "nopcommerce.orders.placed",
            unit: "{orders}",
            description: "Number of orders successfully placed");

    /// <summary>
    /// Tracks order total amounts (store primary currency).
    ///
    /// Operational rationale: an abnormal drop in the p50 bucket can reveal a
    /// discount-rule bug or a pricing misconfiguration long before revenue
    /// reports catch it.  Pair with a Grafana panel showing p50/p95 over time.
    /// </summary>
    public static readonly Histogram<double> OrderTotalAmount =
        _orderMeter.CreateHistogram<double>(
            "nopcommerce.orders.total_amount",
            unit: "{currency_units}",
            description: "Total amount of each placed order in the store currency");
}
