﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Smartstore.Core.Catalog.Discounts;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Data;

namespace Smartstore.Core.Catalog.Pricing.Calculators
{
    /// <summary>
    /// Calculates and applies the discount amount.
    /// Takes into account discounts of products, categories, manufacturers and of bundle items (if per-item pricing is activated).
    /// Also applies the discount amount on the minimum tier price if <see cref="PriceCalculationOptions.IgnorePercentageDiscountOnTierPrices"/> is activated.
    /// </summary>
    [CalculatorUsage(CalculatorTargets.All, CalculatorOrdering.Late)]
    public class DiscountPriceCalculator : IPriceCalculator
    {
        private readonly SmartDbContext _db;
        private readonly IDiscountService _discountService;
        private readonly CatalogSettings _catalogSettings;

        public DiscountPriceCalculator(SmartDbContext db, IDiscountService discountService, CatalogSettings catalogSettings)
        {
            _db = db;
            _discountService = discountService;
            _catalogSettings = catalogSettings;
        }

        public async Task CalculateAsync(CalculatorContext context, CalculatorDelegate next)
        {
            if (context.Options.IgnoreDiscounts)
            {
                // Ignore discounts for this calculation (including discounts for bundle items).
                await next(context);
                return;
            }

            var minTierPrice = context.MinTierPrice ?? 0;

            // Percentage discount on minimum tier price.
            // TODO: (mg) (core) PercentageDiscountOnTierPrices must be tested thoroughly. It is not entirely clear whether this works correctly in all cases.
            if (!context.Options.IgnorePercentageDiscountOnTierPrices && minTierPrice != 0)
            {
                var (discountAmountOnTierPrice, discountOnTierPrice) = await GetDiscountAmountAsync(minTierPrice, context);
                if (discountOnTierPrice != null && discountOnTierPrice.UsePercentage)
                {
                    context.AppliedDiscounts.Add(discountOnTierPrice);
                    context.DiscountAmount += discountAmountOnTierPrice;
                    context.FinalPrice -= discountAmountOnTierPrice;

                    await next(context);
                    return;
                }
            }

            // Discount on final price.
            var (discountAmount, appliedDiscount) = await GetDiscountAmountAsync(context.FinalPrice, context);
            if (appliedDiscount != null)
            {
                context.AppliedDiscounts.Add(appliedDiscount);
                context.DiscountAmount += discountAmount;
                context.FinalPrice -= discountAmount;
            }

            await next(context);
        }

        protected virtual async Task<(decimal DiscountAmount, Discount AppliedDiscount)> GetDiscountAmountAsync(decimal price, CalculatorContext context)
        {
            var discountAmount = 0m;
            Discount appliedDiscount = null;
            var product = context.Product;
            var bundleItem = context.BundleItem;

            if (bundleItem != null)
            {
                if (bundleItem.Discount.HasValue && bundleItem.BundleProduct.BundlePerItemPricing)
                {
                    appliedDiscount = new Discount
                    {
                        UsePercentage = bundleItem.DiscountPercentage,
                        DiscountPercentage = bundleItem.Discount.Value,
                        DiscountAmount = bundleItem.Discount.Value
                    };

                    discountAmount = appliedDiscount.GetDiscountAmount(price);
                }
            }
            else if (!_catalogSettings.IgnoreDiscounts && !product.CustomerEntersPrice)
            {
                // Don't calculate when customer entered price or discounts should be ignored in any case (except for bundle items).
                var applicableDiscounts = await GetApplicableDiscountsAsync(product, context);
                if (applicableDiscounts.Any())
                {
                    appliedDiscount = applicableDiscounts.GetPreferredDiscount(price);
                    if (appliedDiscount != null)
                    {
                        discountAmount = appliedDiscount.GetDiscountAmount(price);
                    }
                }
            }

            return (discountAmount, appliedDiscount);
        }

        protected virtual async Task<ICollection<Discount>> GetApplicableDiscountsAsync(Product product, CalculatorContext context)
        {
            var result = new HashSet<Discount>();
            var batchContext = context.Options.BatchContext;

            // Check discounts assigned to the product.
            // We use the property "HasDiscountsApplied" for performance optimization to avoid unnecessary database calls.
            if (product.HasDiscountsApplied)
            {
                var collectionLoaded = _db.IsCollectionLoaded(product, x => x.AppliedDiscounts);
                var appliedDiscounts = collectionLoaded ? product.AppliedDiscounts : await batchContext.AppliedDiscounts.GetOrLoadAsync(product.Id);

                if (appliedDiscounts != null)
                {
                    await TryAddDiscounts(appliedDiscounts, result, DiscountType.AssignedToSkus, context.Options);
                }
            }

            // Check discounts assigned to categories.
            var categoryDiscounts = await _discountService.GetAllDiscountsAsync(DiscountType.AssignedToCategories);
            if (categoryDiscounts.Any())
            {
                var productCategories = await batchContext.ProductCategories.GetOrLoadAsync(product.Id);

                foreach (var productCategory in productCategories)
                {
                    var category = productCategory.Category;

                    if (category?.HasDiscountsApplied ?? false)
                    {
                        // INFO: AppliedDiscounts are eager loaded already.
                        //await _db.LoadCollectionAsync(category, x => x.AppliedDiscounts);

                        await TryAddDiscounts(category.AppliedDiscounts, result, DiscountType.AssignedToCategories, context.Options);
                    }
                }
            }

            // Check discounts assigned to manufacturers.
            var manufacturerDiscounts = await _discountService.GetAllDiscountsAsync(DiscountType.AssignedToManufacturers);
            if (manufacturerDiscounts.Any())
            {
                var productManufacturers = await batchContext.ProductManufacturers.GetOrLoadAsync(product.Id);

                foreach (var productManufacturer in productManufacturers)
                {
                    var manu = productManufacturer.Manufacturer;

                    if (manu?.HasDiscountsApplied ?? false)
                    {
                        // INFO: AppliedDiscounts are eager loaded already.
                        //await _db.LoadCollectionAsync(manu, x => x.AppliedDiscounts);

                        await TryAddDiscounts(manu.AppliedDiscounts, result, DiscountType.AssignedToManufacturers, context.Options);
                    }
                }
            }

            return result;
        }

        private async Task TryAddDiscounts(ICollection<Discount> source, HashSet<Discount> target, DiscountType type, PriceCalculationOptions options)
        {
            foreach (var discount in source)
            {
                if (discount.DiscountType == type && !target.Contains(discount))
                {
                    bool isValid;
                    if (options.CheckDiscountValidity)
                    {
                        isValid = await _discountService.IsDiscountValidAsync(discount, options.Customer, options.Store);
                    }
                    else
                    {
                        // If validity check is disabled: exclude discounts which require coupon codes or have any usage restriction.
                        isValid = !discount.RequiresCouponCode && discount.DiscountLimitation == DiscountLimitationType.Unlimited;
                    }

                    if (isValid)
                    {
                        target.Add(discount);
                    }
                }
            }
        }
    }
}
