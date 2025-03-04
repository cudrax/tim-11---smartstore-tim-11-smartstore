﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Smartstore.Caching;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Checkout.Attributes;
using Smartstore.Core.Checkout.Cart.Events;
using Smartstore.Core.Common;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Stores;
using Smartstore.Events;

namespace Smartstore.Core.Checkout.Cart
{
    /// <summary>
    /// Shopping cart service methods.
    /// </summary>
    public partial class ShoppingCartService : IShoppingCartService
    {
        // 0 = CustomerId, 1 = CartType, 2 = StoreId
        private const string CART_ITEMS_KEY = "shoppingcartitems:{0}-{1}-{2}";
        private const string CART_ITEMS_PATTERN_KEY = "shoppingcartitems:*";

        private readonly SmartDbContext _db;
        private readonly IWorkContext _workContext;
        private readonly IStoreContext _storeContext;
        private readonly IRequestCache _requestCache;
        private readonly IEventPublisher _eventPublisher;
        private readonly IShoppingCartValidator _cartValidator;
        private readonly IProductAttributeMaterializer _productAttributeMaterializer;
        private readonly ICheckoutAttributeMaterializer _checkoutAttributeMaterializer;
        private readonly ShoppingCartSettings _shoppingCartSettings;
        private readonly Currency _primaryCurrency;

        public ShoppingCartService(
            SmartDbContext db,
            IWorkContext workContext,
            IStoreContext storeContext,
            IRequestCache requestCache,
            IEventPublisher eventPublisher,
            IShoppingCartValidator cartValidator,
            IProductAttributeMaterializer productAttributeMaterializer,
            ICheckoutAttributeMaterializer checkoutAttributeMaterializer,
            ShoppingCartSettings shoppingCartSettings)
        {
            _db = db;
            _workContext = workContext;
            _storeContext = storeContext;
            _requestCache = requestCache;
            _eventPublisher = eventPublisher;
            _cartValidator = cartValidator;
            _productAttributeMaterializer = productAttributeMaterializer;
            _checkoutAttributeMaterializer = checkoutAttributeMaterializer;
            _shoppingCartSettings = shoppingCartSettings;
            _primaryCurrency = storeContext.CurrentStore.PrimaryStoreCurrency;
        }

        public Localizer T { get; set; } = NullLocalizer.Instance;

        public virtual async Task AddItemToCartAsync(AddToCartContext ctx)
        {
            Guard.NotNull(ctx, nameof(ctx));
            Guard.NotNull(ctx.Item, nameof(ctx.Item));

            var customer = ctx.Customer ?? _workContext.CurrentCustomer;

            customer.ShoppingCartItems.Add(ctx.Item);
            await _db.SaveChangesAsync();

            if (ctx.ChildItems.Any())
            {
                foreach (var childItem in ctx.ChildItems)
                {
                    childItem.ParentItemId = ctx.Item.Id;
                }

                customer.ShoppingCartItems.AddRange(ctx.ChildItems);
                await _db.SaveChangesAsync();
            }
        }

        public virtual async Task<bool> AddToCartAsync(AddToCartContext ctx)
        {
            Guard.NotNull(ctx, nameof(ctx));

            // This is called when customer adds a product to cart
            ctx.Customer ??= _workContext.CurrentCustomer;
            ctx.StoreId ??= _storeContext.CurrentStore.Id;

            ctx.Customer.ResetCheckoutData(ctx.StoreId.Value);

            // Get raw attributes from variant query.
            if (ctx.VariantQuery != null)
            {
                await _db.LoadCollectionAsync(ctx.Product, x => x.ProductVariantAttributes, false);

                var (selection, warnings) = await _productAttributeMaterializer.CreateAttributeSelectionAsync(
                    ctx.VariantQuery,
                    ctx.Product.ProductVariantAttributes,
                    ctx.Product.Id,
                    ctx.BundleItemId);

                if (ctx.Product.IsGiftCard)
                {
                    var giftCardInfo = ctx.VariantQuery.GetGiftCardInfo(ctx.Product.Id, ctx.BundleItemId);
                    selection.AddGiftCardInfo(giftCardInfo);
                }

                ctx.RawAttributes = selection.AsJson();
            }

            if (ctx.Product.ProductType == ProductType.BundledProduct && ctx.AttributeSelection.AttributesMap.Any())
            {
                ctx.Warnings.Add(T("ShoppingCart.Bundle.NoAttributes"));

                // For what is this for? It looks like a hack:
                if (ctx.BundleItem != null)
                    return false;
            }

            if (!await _cartValidator.ValidateAccessPermissionsAsync(ctx.Customer, ctx.CartType, ctx.Warnings))
            {
                return false;
            }

            var cart = await GetCartAsync(ctx.Customer, ctx.CartType, ctx.StoreId.Value);

            // Adds required products automatically if it is enabled
            if (ctx.AutomaticallyAddRequiredProducts)
            {
                var requiredProductIds = ctx.Product.ParseRequiredProductIds();
                if (requiredProductIds.Any())
                {
                    var cartProductIds = cart.Items.Select(x => x.Item.ProductId);
                    var missingRequiredProductIds = requiredProductIds.Except(cartProductIds);
                    var missingRequiredProducts = await _db.Products.GetManyAsync(missingRequiredProductIds, false);

                    foreach (var product in missingRequiredProducts)
                    {
                        var item = new ShoppingCartItem
                        {
                            CustomerEnteredPrice = ctx.CustomerEnteredPrice.Amount,
                            RawAttributes = ctx.AttributeSelection.AsJson(),
                            ShoppingCartType = ctx.CartType,
                            StoreId = ctx.StoreId.Value,
                            Quantity = ctx.Quantity,
                            Customer = ctx.Customer,
                            Product = product,
                            BundleItemId = ctx.BundleItem?.Id
                        };

                        await AddItemToCartAsync(new AddToCartContext
                        {
                            Item = item,
                            ChildItems = ctx.ChildItems,
                            Customer = ctx.Customer
                        });
                    }
                }
            }

            // Checks whether required products are still missing
            await _cartValidator.ValidateRequiredProductsAsync(ctx.Product, cart.Items, ctx.Warnings);

            var existingCartItem = ctx.BundleItem == null
                ? cart.FindItemInCart(ctx.CartType, ctx.Product, ctx.AttributeSelection, ctx.CustomerEnteredPrice)?.Item
                : null;

            // Add item to cart (if no warnings accured)
            if (existingCartItem != null && !_shoppingCartSettings.AddProductsToBasketInSinglePositions)
            {
                // Product is already in cart, find existing item
                var newQuantity = ctx.Quantity + existingCartItem.Quantity;

                if (!await _cartValidator.ValidateAddToCartItemAsync(ctx, existingCartItem, cart.Items))
                {
                    return false;
                }

                // Update cart item
                existingCartItem.Quantity = newQuantity;
                existingCartItem.UpdatedOnUtc = DateTime.UtcNow;
                existingCartItem.RawAttributes = ctx.AttributeSelection.AsJson();

                await _db.SaveChangesAsync();
                return true;
            }
            else
            {
                if (!_cartValidator.ValidateItemsMaximumCartQuantity(ctx.CartType, cart.Items.Length, ctx.Warnings))
                {
                    return false;
                }

                // Product is not in cart yet, create new item
                var cartItem = new ShoppingCartItem
                {
                    CustomerEnteredPrice = ctx.CustomerEnteredPrice.Amount,
                    RawAttributes = ctx.RawAttributes,
                    ShoppingCartType = ctx.CartType,
                    StoreId = ctx.StoreId.Value,
                    Quantity = ctx.Quantity,
                    Customer = ctx.Customer,
                    Product = ctx.Product,
                    ProductId = ctx.Product.Id,
                    ParentItemId = null,
                    BundleItemId = ctx.BundleItem?.Id,
                    BundleItem = ctx.BundleItem
                };

                // Validate shopping cart item
                if (!await _cartValidator.ValidateAddToCartItemAsync(ctx, cartItem, cart.Items))
                {
                    return false;
                }

                // Checks whether the product is the parent item of a bundle, or just a simple product.
                if (ctx.BundleItem == null)
                {
                    // Set cart item as item for simple & bundle products, only if its not set by the caller
                    ctx.Item ??= cartItem;
                }
                else
                {
                    // Add item as child of bundle
                    ctx.ChildItems.Add(cartItem);
                }
            }

            _requestCache.RemoveByPattern(CART_ITEMS_PATTERN_KEY);

            // If ctx.Product is a bundle product and the setting to automatically add bundle products is true, try to add all corresponding BundleItems.

            if (ctx.AutomaticallyAddBundleProducts
                && ctx.Product.ProductType == ProductType.BundledProduct
                && ctx.BundleItem == null
                && ctx.Warnings.Count == 0)
            {
                var bundleItems = await _db.ProductBundleItem
                    .ApplyBundledProductsFilter(new[] { ctx.Product.Id }, true)
                    .Include(x => x.Product)
                    .ToListAsync();

                foreach (var bundleItem in bundleItems)
                {
                    bundleItem.BundleProduct = ctx.Item.Product;

                    var bundleItemContext = new AddToCartContext
                    {
                        StoreId = ctx.StoreId,
                        Customer = ctx.Customer,
                        CartType = ctx.CartType,
                        BundleItem = bundleItem,
                        ChildItems = ctx.ChildItems,
                        Product = bundleItem.Product,
                        Quantity = bundleItem.Quantity,
                        VariantQuery = ctx.VariantQuery,
                        RawAttributes = ctx.RawAttributes,
                        CustomerEnteredPrice = ctx.CustomerEnteredPrice,
                        AutomaticallyAddRequiredProducts = ctx.AutomaticallyAddRequiredProducts,
                    };

                    if (!await AddToCartAsync(bundleItemContext))
                    {
                        ctx.Warnings.AddRange(bundleItemContext.Warnings);
                        break;
                    }
                }
            }

            // Add item and its children (if active) to the cart, when it is either a simple product or
            // if it is the parent item of its bundle (bundleItem = null) and no warnings occurred.            
            if (ctx.BundleItem == null && ctx.Warnings.Count == 0)
            {
                await AddItemToCartAsync(ctx);
            }

            return !ctx.Warnings.Any();
        }

        public virtual async Task<bool> CopyAsync(AddToCartContext ctx)
        {
            Guard.NotNull(ctx, nameof(ctx));

            var childItems = ctx.ChildItems;
            ctx.ChildItems = new();

            foreach (var childItem in childItems)
            {
                var childCtx = new AddToCartContext
                {
                    Customer = ctx.Customer,
                    CartType = ctx.CartType,
                    StoreId = ctx.StoreId,
                    BundleItem = childItem.BundleItem,
                    Product = childItem.Product,
                    Quantity = childItem.Quantity,
                    RawAttributes = childItem.RawAttributes,
                    CustomerEnteredPrice = new(childItem.CustomerEnteredPrice, ctx.CustomerEnteredPrice.Currency),
                    ChildItems = ctx.ChildItems
                };

                if (!await AddToCartAsync(childCtx))
                {
                    ctx.Warnings.AddRange(childCtx.Warnings);
                }
            }

            if (ctx.Warnings.Any() || !await AddToCartAsync(ctx))
            {
                return false;
            }

            _requestCache.RemoveByPattern(CART_ITEMS_PATTERN_KEY);

            return !ctx.Warnings.Any();
        }

        public virtual async Task<int> DeleteCartItemAsync(ShoppingCartItem cartItem, bool resetCheckoutData = true, bool removeInvalidCheckoutAttributes = false)
        {
            Guard.NotNull(cartItem, nameof(cartItem));

            var customer = cartItem.Customer;
            var storeId = cartItem.StoreId;

            _db.ShoppingCartItems.Remove(cartItem);

            // Delete child cart items.
            if (customer != null)
            {
                var childItems = await _db.ShoppingCartItems
                    .Where(x => x.CustomerId == customer.Id && x.ParentItemId != null && x.ParentItemId.Value == cartItem.Id && x.Id != cartItem.Id)
                    .ToListAsync();

                _db.ShoppingCartItems.RemoveRange(childItems);
            }

            var num = await _db.SaveChangesAsync();

            _requestCache.RemoveByPattern(CART_ITEMS_PATTERN_KEY);

            if (resetCheckoutData)
            {
                customer?.ResetCheckoutData(storeId);
            }

            if (removeInvalidCheckoutAttributes && cartItem.ShoppingCartType == ShoppingCartType.ShoppingCart && customer != null)
            {
                await RemoveInvalidCheckoutAttributesAsync(customer, storeId);
            }

            return num;
        }

        public virtual async Task<int> DeleteCartAsync(ShoppingCart cart, bool resetCheckoutData = true, bool removeInvalidCheckoutAttributes = false)
        {
            Guard.NotNull(cart, nameof(cart));

            var itemsToDelete = new List<ShoppingCartItem>(cart.Items.Select(x => x.Item));

            // Delete child cart items.
            foreach (var item in cart.Items)
            {
                itemsToDelete.AddRange(item.ChildItems.Select(x => x.Item));
            }

            _db.ShoppingCartItems.RemoveRange(itemsToDelete);

            var num = await _db.SaveChangesAsync();

            _requestCache.RemoveByPattern(CART_ITEMS_PATTERN_KEY);

            if (resetCheckoutData)
            {
                cart.Customer.ResetCheckoutData(cart.StoreId);
            }

            if (removeInvalidCheckoutAttributes && cart.CartType == ShoppingCartType.ShoppingCart)
            {
                await RemoveInvalidCheckoutAttributesAsync(cart.Customer, cart.StoreId);
            }

            return num;
        }

        public virtual Task<ShoppingCart> GetCartAsync(Customer customer = null, ShoppingCartType cartType = ShoppingCartType.ShoppingCart, int storeId = 0)
        {
            customer ??= _workContext.CurrentCustomer;

            var cacheKey = CART_ITEMS_KEY.FormatInvariant(customer.Id, (int)cartType, storeId);

            var result = _requestCache.Get(cacheKey, async () =>
            {
                await LoadCartItemCollection(customer);
                var cartItems = customer.ShoppingCartItems.FilterByCartType(cartType, storeId);

                // Perf: Prefetch (load) all attribute values in any of the attribute definitions across all cart items (including any bundle part).
                await _productAttributeMaterializer.PrefetchProductVariantAttributesAsync(cartItems.Select(x => x.AttributeSelection));

                var organizedItems = await OrganizeCartItemsAsync(cartItems);

                return new ShoppingCart(customer, storeId, organizedItems)
                {
                    CartType = cartType,
                };
            });

            return result;
        }

        public virtual async Task<bool> MigrateCartAsync(Customer fromCustomer, Customer toCustomer)
        {
            Guard.NotNull(fromCustomer, nameof(fromCustomer));
            Guard.NotNull(toCustomer, nameof(toCustomer));

            if (fromCustomer.Id == toCustomer.Id)
            {
                return false;
            }

            var cartItems = await OrganizeCartItemsAsync(fromCustomer.ShoppingCartItems);
            if (!cartItems.Any())
            {
                return false;
            }

            var result = true;
            var firstItem = cartItems[0].Item;

            foreach (var cartItem in cartItems)
            {
                var ctx = new AddToCartContext
                {
                    Product = cartItem.Item.Product,
                    RawAttributes = cartItem.Item.AttributeSelection.AsJson(),
                    CustomerEnteredPrice = new(cartItem.Item.CustomerEnteredPrice, _primaryCurrency),
                    Quantity = cartItem.Item.Quantity,
                    ChildItems = cartItem.ChildItems.Select(x => x.Item).ToList(),
                    Customer = toCustomer,
                    CartType = cartItem.Item.ShoppingCartType,
                    StoreId = cartItem.Item.StoreId,
                };

                if (!await CopyAsync(ctx))
                {
                    result = false;
                }
            }

            if (fromCustomer != null && toCustomer != null)
            {
                _eventPublisher.Publish(new MigrateShoppingCartEvent(fromCustomer, toCustomer, firstItem.StoreId));
            }

            var cart = new ShoppingCart(fromCustomer, firstItem.StoreId, cartItems)
            {
                CartType = firstItem.ShoppingCartType
            };

            await DeleteCartAsync(cart);

            return result;
        }

        public virtual async Task<IList<string>> UpdateCartItemAsync(Customer customer, int cartItemId, int newQuantity, bool resetCheckoutData)
        {
            Guard.NotNull(customer, nameof(customer));

            await LoadCartItemCollection(customer);

            var warnings = new List<string>();
            var cartItem = customer.ShoppingCartItems.FirstOrDefault(x => x.Id == cartItemId && x.ParentItemId == null);
            if (cartItem == null)
            {
                return warnings;
            }

            if (resetCheckoutData)
            {
                customer.ResetCheckoutData(cartItem.StoreId);
            }

            if (newQuantity > 0)
            {
                var ctx = new AddToCartContext
                {
                    Customer = customer,
                    CartType = cartItem.ShoppingCartType,
                    Product = cartItem.Product,
                    StoreId = cartItem.StoreId,
                    RawAttributes = cartItem.AttributeSelection.AsJson(),
                    CustomerEnteredPrice = new Money(cartItem.CustomerEnteredPrice, _primaryCurrency),
                    Quantity = newQuantity,
                    AutomaticallyAddRequiredProducts = false,
                };

                var cart = await GetCartAsync(customer, cartItem.ShoppingCartType, cartItem.StoreId);

                if (await _cartValidator.ValidateAddToCartItemAsync(ctx, cartItem, cart.Items))
                {
                    cartItem.Quantity = newQuantity;
                    cartItem.UpdatedOnUtc = DateTime.UtcNow;

                    await _db.SaveChangesAsync();
                }
                else
                {
                    warnings.AddRange(ctx.Warnings);
                }
            }
            else
            {
                await DeleteCartItemAsync(cartItem, resetCheckoutData, true);
            }

            _requestCache.RemoveByPattern(CART_ITEMS_PATTERN_KEY);

            return warnings;
        }

        protected virtual async Task<List<OrganizedShoppingCartItem>> OrganizeCartItemsAsync(ICollection<ShoppingCartItem> items)
        {
            var result = new List<OrganizedShoppingCartItem>();

            if (items.IsNullOrEmpty())
            {
                return result;
            }

            // Bundle items that require merging of attribute combinations.
            var mergeRequiringItems = new List<ShoppingCartItem>();
            var childItemsMap = items.ToMultimap(x => x.ParentItemId ?? 0, x => x);

            foreach (var parent in items.Where(x => x.ParentItemId == null))
            {
                var parentItem = new OrganizedShoppingCartItem(parent);

                if (childItemsMap.TryGetValues(parent.Id, out var children))
                {
                    parentItem.ChildItems.AddRange(children.Select(x => new OrganizedShoppingCartItem(x)));

                    if (parent.Product?.BundlePerItemPricing ?? false)
                    {
                        // Get cart items where we have to consider attribute combination prices of bundle items.
                        mergeRequiringItems.AddRange(children.Where(x => x.RawAttributes.HasValue() && x.BundleItem != null));
                    }
                }

                result.Add(parentItem);
            }

            if (mergeRequiringItems.Any())
            {
                await _productAttributeMaterializer.MergeWithCombinationAsync(mergeRequiringItems);
            }

            return result;
        }

        /// <summary>
        /// Removes checkout attributes that require shipping, if the cart does not require shipping at all.
        /// </summary>
        protected virtual async Task<int> RemoveInvalidCheckoutAttributesAsync(Customer customer, int storeId)
        {
            var cart = await GetCartAsync(customer, ShoppingCartType.ShoppingCart, storeId);
            if (!cart.IsShippingRequired())
            {
                var attributeSelection = customer.GenericAttributes.CheckoutAttributes;
                var attributes = await _checkoutAttributeMaterializer.MaterializeCheckoutAttributesAsync(attributeSelection);

                var attributeIdsToRemove = attributes
                    .Where(x => x.ShippableProductRequired)
                    .Select(x => x.Id)
                    .Distinct()
                    .ToArray();

                if (attributeIdsToRemove.Any())
                {
                    attributeSelection.RemoveAttributes(attributeIdsToRemove);

                    customer.GenericAttributes.CheckoutAttributes = attributeSelection;

                    return await _db.SaveChangesAsync();
                }
            }

            return 0;
        }

        private async Task LoadCartItemCollection(Customer customer, bool force = false)
        {
            await _db.LoadCollectionAsync(customer, x => x.ShoppingCartItems, force, x =>
            {
                return x.Include(y => y.Product)
                    .ThenInclude(y => y.ProductVariantAttributes);
            });
        }
    }
}