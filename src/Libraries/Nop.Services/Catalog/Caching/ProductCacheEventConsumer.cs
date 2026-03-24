using System.Diagnostics;
using Nop.Core.Caching;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Orders;
using Nop.Services.Caching;
using Nop.Services.Discounts;
using Nop.Services.Telemetry;

namespace Nop.Services.Catalog.Caching;

/// <summary>
/// Represents a product cache event consumer
/// </summary>
public partial class ProductCacheEventConsumer : CacheEventConsumer<Product>
{
    /// <summary>
    /// Clear cache data
    /// </summary>
    /// <param name="entity">Entity</param>
    /// <param name="entityEventType">Entity event type</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    protected override async Task ClearCacheAsync(Product entity, EntityEventType entityEventType)
    {
        using var activity = NopTelemetry.CatalogSource.StartActivity("catalog.cache.clear");
        activity?.SetTag("product.id", entity.Id);
        activity?.SetTag("event_type", entityEventType.ToString());

        var sw = Stopwatch.StartNew();
        var cleared = 0;

        await RemoveByPrefixAsync(NopCatalogDefaults.ProductManufacturersByProductPrefix, entity); cleared++;
        await RemoveAsync(NopCatalogDefaults.ProductsHomepageCacheKey); cleared++;
        await RemoveByPrefixAsync(NopCatalogDefaults.ProductPricePrefix, entity); cleared++;
        await RemoveByPrefixAsync(NopCatalogDefaults.ProductMultiplePricePrefix, entity); cleared++;
        await RemoveByPrefixAsync(NopEntityCacheDefaults<ShoppingCartItem>.AllPrefix); cleared++;
        await RemoveByPrefixAsync(NopCatalogDefaults.FeaturedProductIdsPrefix); cleared++;

        if (entityEventType == EntityEventType.Delete)
        {
            await RemoveByPrefixAsync(NopCatalogDefaults.FilterableSpecificationAttributeOptionsPrefix); cleared++;
            await RemoveByPrefixAsync(NopCatalogDefaults.ManufacturersByCategoryPrefix); cleared++;
        }

        await RemoveAsync(NopDiscountDefaults.AppliedDiscountsCacheKey, nameof(Product), entity); cleared++;

        // base clears: ByIds prefix, All prefix, and ById key (if not insert)
        cleared += entityEventType == EntityEventType.Insert ? 2 : 3;
        await base.ClearCacheAsync(entity, entityEventType);

        sw.Stop();

        activity?.SetTag("cache.keys_cleared", cleared);

        NopTelemetry.CacheClearDuration.Record(sw.Elapsed.TotalMilliseconds,
            new TagList { { "event_type", entityEventType.ToString() } });
    }
}
