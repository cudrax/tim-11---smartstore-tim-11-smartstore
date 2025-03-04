﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Net;
using System.Threading.Tasks;
using AngleSharp.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Smartstore.Admin.Models.Catalog;
using Smartstore.Collections;
using Smartstore.ComponentModel;
using Smartstore.Core;
using Smartstore.Core.Catalog;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Catalog.Brands;
using Smartstore.Core.Catalog.Categories;
using Smartstore.Core.Catalog.Discounts;
using Smartstore.Core.Catalog.Pricing;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Catalog.Search;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Common.Settings;
using Smartstore.Core.Content.Media;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Logging;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Core.Seo;
using Smartstore.Core.Stores;
using Smartstore.Web.Controllers;
using Smartstore.Web.Modelling;
using Smartstore.Web.Models.DataGrid;
using Smartstore.Web.Models;
using Smartstore.Web.Rendering;
using Smartstore.Web.TagHelpers.Shared;
using Smartstore.Core.Catalog.Products.Utilities;

namespace Smartstore.Admin.Controllers
{
    public partial class ProductController : AdminController
    {
        private readonly SmartDbContext _db;
        private readonly IProductService _productService;           
        private readonly IUrlService _urlService;                   
        private readonly IWorkContext _workContext;
        private readonly ILocalizedEntityService _localizedEntityService;
        private readonly IMediaService _mediaService;
        private readonly IProductTagService _productTagService;
        private readonly IAclService _aclService;
        private readonly IStoreMappingService _storeMappingService;
        private readonly IDateTimeHelper _dateTimeHelper;
        private readonly IDiscountService _discountService;
        private readonly Lazy<IProductCloner> _productCloner;
        private readonly Lazy<ICategoryService> _categoryService;
        private readonly Lazy<IManufacturerService> _manufacturerService;
        private readonly Lazy<IProductAttributeService> _productAttributeService;
        private readonly Lazy<IProductAttributeMaterializer> _productAttributeMaterializer;
        private readonly Lazy<IStockSubscriptionService> _stockSubscriptionService;
        private readonly Lazy<IShoppingCartService> _shoppingCartService;
        private readonly Lazy<IShoppingCartValidator> _shoppingCartValidator;
        private readonly Lazy<IProductAttributeFormatter> _productAttributeFormatter;
        private readonly Lazy<IDownloadService> _downloadService;
        private readonly Lazy<ICatalogSearchService> _catalogSearchService;
        private readonly Lazy<ProductUrlHelper> _productUrlHelper;
        private readonly AdminAreaSettings _adminAreaSettings;
        private readonly CatalogSettings _catalogSettings;
        private readonly MeasureSettings _measureSettings;
        private readonly SeoSettings _seoSettings;
        private readonly MediaSettings _mediaSettings;
        private readonly SearchSettings _searchSettings;

        public ProductController(
            SmartDbContext db,
            IProductService productService,
            IUrlService urlService,
            IWorkContext workContext,
            ILocalizedEntityService localizedEntityService,
            IMediaService mediaService,
            IProductTagService productTagService,
            IAclService aclService,
            IStoreMappingService storeMappingService,
            IDateTimeHelper dateTimeHelper,
            IDiscountService discountService,
            Lazy<IProductCloner> productCloner,
            Lazy<ICategoryService> categoryService,
            Lazy<IManufacturerService> manufacturerService,
            Lazy<IProductAttributeService> productAttributeService,
            Lazy<IProductAttributeMaterializer> productAttributeMaterializer,
            Lazy<IStockSubscriptionService> stockSubscriptionService,
            Lazy<IShoppingCartService> shoppingCartService,
            Lazy<IShoppingCartValidator> shoppingCartValidator,
            Lazy<IProductAttributeFormatter> productAttributeFormatter,
            Lazy<IDownloadService> downloadService,
            Lazy<ICatalogSearchService> catalogSearchService,
            Lazy<ProductUrlHelper> productUrlHelper,
            AdminAreaSettings adminAreaSettings,
            CatalogSettings catalogSettings,
            MeasureSettings measureSettings,
            SeoSettings seoSettings,
            MediaSettings mediaSettings,
            SearchSettings searchSettings)
        {
            _db = db;
            _productService = productService;
            _categoryService = categoryService;
            _manufacturerService = manufacturerService;
            _urlService = urlService;
            _workContext = workContext;
            _localizedEntityService = localizedEntityService;
            _mediaService = mediaService;
            _productTagService = productTagService;
            _productCloner = productCloner;
            _aclService = aclService;
            _storeMappingService = storeMappingService;
            _dateTimeHelper = dateTimeHelper;
            _discountService = discountService;
            _productAttributeService = productAttributeService;
            _productAttributeMaterializer = productAttributeMaterializer;
            _stockSubscriptionService = stockSubscriptionService;
            _shoppingCartService = shoppingCartService;
            _shoppingCartValidator = shoppingCartValidator;
            _productAttributeFormatter = productAttributeFormatter;
            _downloadService = downloadService;
            _catalogSearchService = catalogSearchService;
            _productUrlHelper = productUrlHelper;
            _adminAreaSettings = adminAreaSettings;
            _catalogSettings = catalogSettings;
            _measureSettings = measureSettings;
            _seoSettings = seoSettings;
            _mediaSettings = mediaSettings;
            _searchSettings = searchSettings;
        }

        #region Product list / create / edit / delete

        public IActionResult Index()
        {
            return RedirectToAction(nameof(List));
        }

        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> List(ProductListModel model)
        {
            model.DisplayProductPictures = _adminAreaSettings.DisplayProductPictures;
            model.IsSingleStoreMode = Services.StoreContext.IsSingleStoreMode();

            foreach (var c in (await _categoryService.Value.GetCategoryTreeAsync(includeHidden: true)).FlattenNodes(false))
            {
                model.AvailableCategories.Add(new SelectListItem { Text = c.GetCategoryNameIndented(), Value = c.Id.ToString() });
            }

            foreach (var m in await _db.Manufacturers.AsNoTracking().ApplyStandardFilter(true).Select(x => new { x.Name, x.Id }).ToListAsync())
            {
                model.AvailableManufacturers.Add(new SelectListItem { Text = m.Name, Value = m.Id.ToString() });
            }

            model.AvailableProductTypes = ProductType.SimpleProduct.ToSelectList(false).ToList();

            return View(model);
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.Delete)]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _db.Products.FindByIdAsync(id);
            _db.Products.Remove(product);
            await _db.SaveChangesAsync();

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.DeleteProduct, T("ActivityLog.DeleteProduct"), product.Name);

            NotifySuccess(T("Admin.Catalog.Products.Deleted"));
            return RedirectToAction(nameof(List));
        }

        [Permission(Permissions.Catalog.Product.Create)]
        public async Task<IActionResult> Create()
        {
            var model = new ProductModel();
            await PrepareProductModelAsync(model, null, true, true);
            AddLocales(model.Locales);

            return View(model);
        }

        [HttpPost, ParameterBasedOnFormName("save-continue", "continueEditing")]
        [Permission(Permissions.Catalog.Product.Create)]
        public async Task<IActionResult> Create(ProductModel model, bool continueEditing, IFormCollection form)
        {
            if (model.DownloadFileVersion.HasValue() && model.DownloadId != null)
            {
                try
                {
                    var test = SemanticVersion.Parse(model.DownloadFileVersion);
                }
                catch
                {
                    ModelState.AddModelError("FileVersion", T("Admin.Catalog.Products.Download.SemanticVersion.NotValid"));
                }
            }

            if (ModelState.IsValid)
            {
                var product = new Product();

                await MapModelToProductAsync(model, product, form);

                product.StockQuantity = 10000;
                product.OrderMinimumQuantity = 1;
                product.OrderMaximumQuantity = 100;
                product.HideQuantityControl = false;
                product.IsShippingEnabled = true;
                product.AllowCustomerReviews = true;
                product.Published = true;
                product.MaximumCustomerEnteredPrice = 1000;

                if (product.ProductType == ProductType.BundledProduct)
                {
                    product.BundleTitleText = T("Products.Bundle.BundleIncludes");
                }

                _db.Products.Add(product);
                await _db.SaveChangesAsync();

                await UpdateDataOfExistingProductAsync(product, model, false);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.AddNewProduct, T("ActivityLog.AddNewProduct"), product.Name);

                if (continueEditing)
                {
                    // ensure that the same tab gets selected in edit view
                    var selectedTab = TempData["SelectedTab.product-edit"] as SelectedTabInfo;
                    if (selectedTab != null)
                    {
                        selectedTab.Path = Url.Action("Edit", new RouteValueDictionary { { "id", product.Id } });
                    }
                }

                NotifySuccess(T("Admin.Catalog.Products.Added"));
                return continueEditing ? RedirectToAction(nameof(Edit), new { id = product.Id }) : RedirectToAction(nameof(List));
            }

            // If we got this far something failed. Redisplay form.
            await PrepareProductModelAsync(model, null, false, true);

            return View(model);
        }

        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> Edit(int id)
        {
            var product = await _db.Products
                .Include(x => x.ProductTags)
                .Include(x => x.AppliedDiscounts)
                .FindByIdAsync(id);

            if (product == null)
            {
                NotifyWarning(T("Products.NotFound", id));
                return RedirectToAction(nameof(List));
            }

            if (product.Deleted)
            {
                NotifyWarning(T("Products.Deleted", id));
                return RedirectToAction(nameof(List));
            }

            var model = await MapperFactory.MapAsync<Product, ProductModel>(product);
            await PrepareProductModelAsync(model, product, false, false);

            AddLocales(model.Locales, async (locale, languageId) =>
            {
                locale.Name = product.GetLocalized(x => x.Name, languageId, false, false);
                locale.ShortDescription = product.GetLocalized(x => x.ShortDescription, languageId, false, false);
                locale.FullDescription = product.GetLocalized(x => x.FullDescription, languageId, false, false);
                locale.MetaKeywords = product.GetLocalized(x => x.MetaKeywords, languageId, false, false);
                locale.MetaDescription = product.GetLocalized(x => x.MetaDescription, languageId, false, false);
                locale.MetaTitle = product.GetLocalized(x => x.MetaTitle, languageId, false, false);
                locale.SeName = await product.GetActiveSlugAsync(languageId, false, false);
                locale.BundleTitleText = product.GetLocalized(x => x.BundleTitleText, languageId, false, false);
            });

            return View(model);
        }

        [HttpPost, ParameterBasedOnFormName("save-continue", "continueEditing")]
        [Permission(Permissions.Catalog.Product.Update)]
        public async Task<IActionResult> Edit(ProductModel model, bool continueEditing, IFormCollection form)
        {
            var product = await _db.Products
                .Include(x => x.AppliedDiscounts)
                .Include(x => x.ProductTags)
                .FindByIdAsync(model.Id);

            if (product == null)
            {
                NotifyWarning(T("Products.NotFound", model.Id));
                return RedirectToAction(nameof(List));
            }

            if (product.Deleted)
            {
                NotifyWarning(T("Products.Deleted", model.Id));
                return RedirectToAction(nameof(List));
            }

            await UpdateDataOfProductDownloadsAsync(model);

            if (ModelState.IsValid)
            {
                await MapModelToProductAsync(model, product, form);
                await UpdateDataOfExistingProductAsync(product, model, true);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditProduct, T("ActivityLog.EditProduct"), product.Name);

                NotifySuccess(T("Admin.Catalog.Products.Updated"));
                return continueEditing ? RedirectToAction(nameof(Edit), new { id = product.Id }) : RedirectToAction(nameof(List));
            }

            // If we got this far something failed. Redisplay form.
            await PrepareProductModelAsync(model, product, false, true);

            return View(model);
        }


        #endregion

        #region Misc 

        /// <summary>
        /// (AJAX) Gets a list of all products.
        /// </summary>
        /// <param name="page">Zero based page index.</param>
        /// <param name="term">Optional search term.</param>
        /// <param name="selectedIds">Selected product identifiers.</param>
        public async Task<IActionResult> AllProducts(int page, string term, string selectedIds)
        {
            const int pageSize = 100;
            IEnumerable<Product> products = null;
            var hasMoreData = true;
            var skip = page * pageSize;
            var ids = selectedIds.ToIntArray();
            var fields = new List<string> { "name" };

            if (_searchSettings.SearchFields.Contains("sku"))
            {
                fields.Add("sku");
            }
            if (_searchSettings.SearchFields.Contains("shortdescription"))
            {
                fields.Add("shortdescription");
            }

            var searchQuery = new CatalogSearchQuery(fields.ToArray(), term);

            if (_searchSettings.UseCatalogSearchInBackend)
            {
                searchQuery = searchQuery
                    .Slice(skip, pageSize)
                    .SortBy(ProductSorting.NameAsc);

                var searchResult = await _catalogSearchService.Value.SearchAsync(searchQuery);
                var hits = await searchResult.GetHitsAsync();

                hasMoreData = hits.HasNextPage;
                products = hits;
            }
            else
            {
                var query = _catalogSearchService.Value.PrepareQuery(searchQuery);

                hasMoreData = (page + 1) * pageSize < query.Count();
                products = await query
                    .Select(x => new Product
                    {
                        Id = x.Id,
                        Name = x.Name,
                        Sku = x.Sku
                    })
                    .OrderBy(x => x.Name)
                    .Skip(skip)
                    .Take(pageSize)
                    .ToListAsync();
            }

            var items = products.Select(x => new ChoiceListItem
            {
                Id = x.Id.ToString(),
                Text = x.Name,
                Hint = x.Sku,
                Selected = ids.Contains(x.Id)
            })
            .ToList();

            return new JsonResult(new
            {
                hasMoreData,
                results = items
            });
        }

        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> LoadEditTab(int id, string tabName, string viewPath = null)
        {
            try
            {
                if (id == 0)
                {
                    // If id is 0 we're in create mode.
                    return PartialView("_Create.SaveFirst");
                }

                if (tabName.IsEmpty())
                {
                    return Content("A unique tab name has to specified (route parameter: tabName)");
                }

                var product = await _db.Products
                    .Include(x => x.AppliedDiscounts)
                    .Include(x => x.ProductTags)
                    .FindByIdAsync(id, false);

                var model = await MapperFactory.MapAsync<Product, ProductModel>(product);

                await PrepareProductModelAsync(model, product, false, false);

                AddLocales(model.Locales, async (locale, languageId) =>
                {
                    locale.Name = product.GetLocalized(x => x.Name, languageId, false, false);
                    locale.ShortDescription = product.GetLocalized(x => x.ShortDescription, languageId, false, false);
                    locale.FullDescription = product.GetLocalized(x => x.FullDescription, languageId, false, false);
                    locale.MetaKeywords = product.GetLocalized(x => x.MetaKeywords, languageId, false, false);
                    locale.MetaDescription = product.GetLocalized(x => x.MetaDescription, languageId, false, false);
                    locale.MetaTitle = product.GetLocalized(x => x.MetaTitle, languageId, false, false);
                    locale.SeName = await product.GetActiveSlugAsync(languageId, false, false);
                    locale.BundleTitleText = product.GetLocalized(x => x.BundleTitleText, languageId, false, false);
                });

                return PartialView(viewPath.NullEmpty() ?? "_CreateOrUpdate." + tabName, model);
            }
            catch (Exception ex)
            {
                return Content("Error while loading template: " + ex.Message);
            }
        }

        [HttpPost, ActionName("List")]
        [FormValueRequired("go-to-product-by-sku")]
        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> GoToSku(ProductListModel model)
        {
            var sku = model.GoDirectlyToSku;

            if (sku.HasValue())
            {
                var product = await _db.Products
                    .ApplySkuFilter(sku)
                    .Select(x => new { x.Id })
                    .FirstOrDefaultAsync();

                if (product != null)
                {
                    return RedirectToAction(nameof(Edit), new { id = product.Id });
                }

                var combination = await _db.ProductVariantAttributeCombinations
                    .AsNoTracking()
                    .ApplySkuFilter(sku)
                    .Select(x => new { x.ProductId, ProductDeleted = x.Product.Deleted })
                    .FirstOrDefaultAsync();

                if (combination != null)
                {
                    return RedirectToAction(nameof(Edit), new { id = combination.ProductId });
                }
            }

            // Not found.
            return RedirectToAction(nameof(List));
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.Create)]
        public async Task<IActionResult> CopyProduct(ProductModel model)
        {
            var copyModel = model.CopyProductModel;
            try
            {
                Product newProduct = null;
                // Lets just load this untracked as nearly all navigation properties are needed in order to copy successfully.
                // We just eager load the most common properties.
                var product = await _db.Products
                    .Include(x => x.ProductCategories)
                    .Include(x => x.ProductManufacturers)
                    .Include(x => x.ProductSpecificationAttributes)
                    .Include(x => x.ProductVariantAttributes)
                    .Include(x => x.ProductVariantAttributeCombinations)
                    .FindByIdAsync(copyModel.Id);

                for (var i = 1; i <= copyModel.NumberOfCopies; ++i)
                {
                    var newName = copyModel.NumberOfCopies > 1 ? $"{copyModel.Name} {i}" : copyModel.Name;
                    newProduct = await _productCloner.Value.CloneProductAsync(product, newName, copyModel.Published);
                }

                if (newProduct != null)
                {
                    NotifySuccess(T("Admin.Common.TaskSuccessfullyProcessed"));
                    return RedirectToAction(nameof(Edit), new { id = newProduct.Id });
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                NotifyError(ex.ToAllMessages());
            }

            return RedirectToAction(nameof(Edit), new { id = copyModel.Id });
        }

        #endregion

        #region Product categories

        [HttpPost]
        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> ProductCategoryList(int productId)
        {
            var model = new GridModel<ProductModel.ProductCategoryModel>();
            var productCategories = await _categoryService.Value.GetProductCategoriesByProductIdsAsync(new[] { productId }, true);
            var productCategoriesModel = await productCategories
                .AsQueryable()
                .SelectAsync(async x =>
                {
                    var node = await _categoryService.Value.GetCategoryTreeAsync(x.CategoryId, true);
                    return new ProductModel.ProductCategoryModel
                    {
                        Id = x.Id,
                        Category = node != null ? _categoryService.Value.GetCategoryPath(node, aliasPattern: "<span class='badge badge-secondary'>{0}</span>") : string.Empty,
                        ProductId = x.ProductId,
                        CategoryId = x.CategoryId,
                        IsFeaturedProduct = x.IsFeaturedProduct,
                        DisplayOrder = x.DisplayOrder,
                        IsSystemMapping = x.IsSystemMapping,
                        EditUrl = Url.Action("Edit", "Category", new { id = x.CategoryId })
                    };
                })
                .AsyncToList();

            model.Rows = productCategoriesModel;
            model.Total = productCategoriesModel.Count;

            return Json(model);
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditCategory)]
        public async Task<IActionResult> ProductCategoryInsert(ProductModel.ProductCategoryModel model, int productId)
        {
            var alreadyAssigned = await _db.ProductCategories.AnyAsync(x => x.CategoryId == model.CategoryId && x.ProductId == productId);

            if (alreadyAssigned)
            {
                NotifyError(T("Admin.Catalog.Products.Categories.NoDuplicatesAllowed"));
                return Json(new { success = false });
            }

            var productCategory = new ProductCategory
            {
                ProductId = productId,
                CategoryId = model.CategoryId,
                IsFeaturedProduct = model.IsFeaturedProduct,
                DisplayOrder = model.DisplayOrder
            };

            try
            {
                _db.ProductCategories.Add(productCategory);

                var mru = new TrimmedBuffer<string>(
                    _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedCategories,
                    model.Category,
                    _catalogSettings.MostRecentlyUsedCategoriesMaxSize);

                _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedCategories = mru.ToString();
                await _db.SaveChangesAsync();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditCategory)]
        public async Task<IActionResult> ProductCategoryUpdate(ProductModel.ProductCategoryModel model)
        {
            var productCategory = await _db.ProductCategories.FindByIdAsync(model.Id);
            var categoryChanged = model.CategoryId != productCategory.CategoryId;

            if (categoryChanged)
            {
                var alreadyAssigned = await _db.ProductCategories.AnyAsync(x => x.CategoryId == model.CategoryId && x.ProductId == model.ProductId);

                if (alreadyAssigned)
                {
                    NotifyError(T("Admin.Catalog.Products.Categories.NoDuplicatesAllowed"));
                    return Json(new { success = false });
                }
            }
            
            productCategory.CategoryId = model.CategoryId;
            productCategory.IsFeaturedProduct = model.IsFeaturedProduct;
            productCategory.DisplayOrder = model.DisplayOrder;

            try
            {
                if (categoryChanged)
                {
                    var mru = new TrimmedBuffer<string>(
                        _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedCategories,
                        model.Category,
                        _catalogSettings.MostRecentlyUsedCategoriesMaxSize);

                    _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedCategories = mru.ToString();
                }

                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditCategory)]
        public async Task<IActionResult> ProductCategoryDelete(GridSelection selection)
        {
            var ids = selection.GetEntityIds();
            var numDeleted = 0;

            if (ids.Any())
            {
                var toDelete = await _db.ProductCategories.GetManyAsync(ids);
                _db.ProductCategories.RemoveRange(toDelete);
                numDeleted = await _db.SaveChangesAsync();
            }

            return Json(new { Success = true, Count = numDeleted });
        }

        #endregion

        #region Product manufacturers

        [HttpPost]
        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> ProductManufacturerList(int productId)
        {
            var model = new GridModel<ProductModel.ProductManufacturerModel>();
            var productManufacturers = await _manufacturerService.Value.GetProductManufacturersByProductIdsAsync(new[] { productId }, true);
            var productManufacturersModel = productManufacturers
                .AsQueryable()
                .ToList()
                .Select(x =>
                {
                    return new ProductModel.ProductManufacturerModel
                    {
                        Id = x.Id,
                        Manufacturer = x.Manufacturer.Name,
                        ProductId = x.ProductId,
                        ManufacturerId = x.ManufacturerId,
                        IsFeaturedProduct = x.IsFeaturedProduct,
                        DisplayOrder = x.DisplayOrder,
                        EditUrl = Url.Action("Edit", "Manufacturer", new { id = x.ManufacturerId })
                    };
                })
                .ToList();

            model.Rows = productManufacturersModel;
            model.Total = productManufacturersModel.Count;

            return Json(model);
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditManufacturer)]
        public async Task<IActionResult> ProductManufacturerInsert(ProductModel.ProductManufacturerModel model, int productId)
        {
            var alreadyAssigned = await _db.ProductManufacturers.AnyAsync(x => x.ManufacturerId == model.ManufacturerId && x.ProductId == productId);

            if (alreadyAssigned)
            {
                NotifyError(T("Admin.Catalog.Products.Manufacturers.NoDuplicatesAllowed"));
                return Json(new { success = false });
            }

            var productManufacturer = new ProductManufacturer
            {
                ProductId = productId,
                ManufacturerId = model.ManufacturerId,
                IsFeaturedProduct = model.IsFeaturedProduct,
                DisplayOrder = model.DisplayOrder
            };

            try
            {
                _db.ProductManufacturers.Add(productManufacturer);

                var mru = new TrimmedBuffer<string>(
                    _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedManufacturers,
                    model.Manufacturer,
                    _catalogSettings.MostRecentlyUsedManufacturersMaxSize);

                _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedManufacturers = mru.ToString();
                await _db.SaveChangesAsync();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditManufacturer)]
        public async Task<IActionResult> ProductManufacturerUpdate(ProductModel.ProductManufacturerModel model)
        {
            var productManufacturer = await _db.ProductManufacturers.FindByIdAsync(model.Id);
            var manufacturerChanged = model.ManufacturerId != productManufacturer.ManufacturerId;

            if (manufacturerChanged)
            {
                var alreadyAssigned = await _db.ProductManufacturers.AnyAsync(x => x.ManufacturerId == model.ManufacturerId && x.ProductId == model.ProductId);

                if (alreadyAssigned)
                {
                    NotifyError(T("Admin.Catalog.Products.Manufacturers.NoDuplicatesAllowed"));
                    return Json(new { success = false });
                }
            }

            productManufacturer.ManufacturerId = model.ManufacturerId;
            productManufacturer.IsFeaturedProduct = model.IsFeaturedProduct;
            productManufacturer.DisplayOrder = model.DisplayOrder;

            try
            {
                if (manufacturerChanged)
                {
                    var mru = new TrimmedBuffer<string>(
                        _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedManufacturers,
                        model.Manufacturer,
                        _catalogSettings.MostRecentlyUsedManufacturersMaxSize);

                    _workContext.CurrentCustomer.GenericAttributes.MostRecentlyUsedManufacturers = mru.ToString();
                }

                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditManufacturer)]
        public async Task<IActionResult> ProductManufacturerDelete(GridSelection selection)
        {
            var ids = selection.GetEntityIds();
            var numDeleted = 0;

            if (ids.Any())
            {
                var toDelete = await _db.ProductManufacturers.GetManyAsync(ids);
                _db.ProductManufacturers.RemoveRange(toDelete);
                numDeleted = await _db.SaveChangesAsync();
            }

            return Json(new { Success = true, Count = numDeleted });
        }

        #endregion

        #region Product pictures

        [HttpPost]
        public async Task<IActionResult> SortPictures(string pictures, int entityId)
        {
            var response = new List<dynamic>();

            try
            {
                var files = await _db.ProductMediaFiles
                    .ApplyProductFilter(entityId)
                    .ToListAsync();

                var pictureIds = new HashSet<int>(pictures.ToIntArray());
                var ordinal = 5;

                foreach (var id in pictureIds)
                {
                    var productPicture = files.Where(x => x.Id == id).FirstOrDefault();
                    if (productPicture != null)
                    {
                        productPicture.DisplayOrder = ordinal;

                        // Add all relevant data of product picture to response.
                        dynamic file = new
                        {
                            productPicture.DisplayOrder,
                            productPicture.MediaFileId,
                            EntityMediaId = productPicture.Id
                        };

                        response.Add(file);
                    }
                    ordinal += 5;
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                NotifyError(ex.Message);
                return StatusCode(501, Json(ex.Message));
            }

            return Json(new { success = true, response });
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditPicture)]
        public async Task<IActionResult> ProductMediaFilesAdd(string mediaFileIds, int entityId)
        {
            var ids = mediaFileIds
                .ToIntArray()
                .Distinct()
                .ToArray();

            if (!ids.Any())
            {
                throw new ArgumentException("Missing picture identifiers.");
            }

            var success = false;
            var product = await _db.Products.FindByIdAsync(entityId, false);
            if (product == null)
            {
                throw new ArgumentException(T("Products.NotFound", entityId));
            }

            var response = new List<dynamic>();
            var existingFiles = product.ProductPictures.Select(x => x.MediaFileId).ToList();
            var files = (await _mediaService.GetFilesByIdsAsync(ids, MediaLoadFlags.AsNoTracking)).ToDictionary(x => x.Id);

            foreach (var id in ids)
            {
                var exists = existingFiles.Contains(id);

                // No duplicate assignments!
                if (!exists)
                {
                    var productPicture = new ProductMediaFile
                    {
                        MediaFileId = id,
                        ProductId = entityId
                    };


                    _db.ProductMediaFiles.Add(productPicture);
                    await _db.SaveChangesAsync();

                    files.TryGetValue(id, out var file);

                    success = true;

                    dynamic respObj = new
                    {
                        MediaFileId = id,
                        ProductMediaFileId = productPicture.Id,
                        file?.Name
                    };

                    response.Add(respObj);
                }
            }

            return Json(new
            {
                success,
                response,
                message = T("Admin.Product.Picture.Added").JsValue.ToString()
            });
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditPicture)]
        public async Task<IActionResult> ProductPictureDelete(int id)
        {
            var productPicture = await _db.ProductMediaFiles.FindByIdAsync(id);

            if (productPicture != null)
            {
                _db.ProductMediaFiles.Remove(productPicture);
                await _db.SaveChangesAsync();
            }

            // TODO: (mm) (mc) OPTIONALLY delete file!
            //var file = _mediaService.GetFileById(productPicture.MediaFileId);
            //if (file != null)
            //{
            //    _mediaService.DeleteFile(file.File, true);
            //}

            NotifySuccess(T("Admin.Catalog.Products.ProductPictures.Delete.Success"));
            return StatusCode((int)HttpStatusCode.OK);
        }

        #endregion

        #region Tier prices

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTierPrice)]
        public async Task<IActionResult> TierPriceList(GridCommand command, int productId)
        {
            var model = new GridModel<ProductModel.TierPriceModel>();
            var tierPrices = await _db.TierPrices
                .Where(x => x.ProductId == productId)
                .ApplyGridCommand(command)
                .ToPagedList(command)
                .LoadAsync();

            var product = await _db.Products
                .Include(x => x.TierPrices)
                .ThenInclude(x => x.CustomerRole)
                .FindByIdAsync(productId, false);

            string allRolesString = T("Admin.Catalog.Products.TierPrices.Fields.CustomerRole.AllRoles");
            string allStoresString = T("Admin.Common.StoresAll");
            string deletedString = $"[{T("Admin.Common.Deleted")}]";

            var customerRoles = new Dictionary<int, CustomerRole>();
            var stores = new Dictionary<int, Store>();

            if (product.TierPrices.Any())
            {
                var customerRoleIds = new HashSet<int>(product.TierPrices
                    .Select(x => x.CustomerRoleId ?? 0)
                    .Where(x => x != 0));

                var customerRolesQuery = _db.CustomerRoles
                    .AsNoTracking()
                    .ApplyStandardFilter(true)
                    .AsQueryable();

                customerRoles = (await customerRolesQuery
                    .Where(x => customerRoleIds.Contains(x.Id))
                    .ToListAsync())
                    .ToDictionary(x => x.Id);

                stores = Services.StoreContext.GetAllStores().ToDictionary(x => x.Id);
            }

            var tierPricesModel = tierPrices
                .Select(x =>
                {
                    var tierPriceModel = new ProductModel.TierPriceModel
                    {
                        Id = x.Id,
                        StoreId = x.StoreId,
                        CustomerRoleId = x.CustomerRoleId ?? 0,
                        ProductId = x.ProductId,
                        Quantity = x.Quantity,
                        CalculationMethodId = (int)x.CalculationMethod,
                        Price1 = x.Price
                    };

                    tierPriceModel.CalculationMethod = x.CalculationMethod switch
                    {
                        TierPriceCalculationMethod.Fixed => T("Admin.Product.Price.Tierprices.Fixed").Value,
                        TierPriceCalculationMethod.Adjustment => T("Admin.Product.Price.Tierprices.Adjustment").Value,
                        TierPriceCalculationMethod.Percental => T("Admin.Product.Price.Tierprices.Percental").Value,
                        _ => x.CalculationMethod.ToString(),
                    };

                    if (x.CustomerRoleId.HasValue)
                    {
                        customerRoles.TryGetValue(x.CustomerRoleId.Value, out var role);
                        tierPriceModel.CustomerRole = role?.Name.NullEmpty() ?? allRolesString;
                    }
                    else
                    {
                        tierPriceModel.CustomerRole = allRolesString;
                    }

                    if (x.StoreId > 0)
                    {
                        stores.TryGetValue(x.StoreId, out var store);
                        tierPriceModel.Store = store?.Name.NullEmpty() ?? deletedString;
                    }
                    else
                    {
                        tierPriceModel.Store = allStoresString;
                    }

                    return tierPriceModel;
                })
                .ToList();

            model.Rows = tierPricesModel;
            model.Total = tierPrices.TotalCount;

            return Json(model);
        }


        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTierPrice)]
        public async Task<IActionResult> TierPriceInsert(ProductModel.TierPriceModel model, int productId)
        {
            var tierPrice = new TierPrice
            {
                ProductId = productId,
                StoreId = model.StoreId ?? 0,
                CustomerRoleId = model.CustomerRoleId,
                Quantity = model.Quantity,
                Price = model.Price1 ?? 0,
                CalculationMethod = (TierPriceCalculationMethod)model.CalculationMethodId
            };

            try
            {
                _db.TierPrices.Add(tierPrice);
                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTierPrice)]
        public async Task<IActionResult> TierPriceUpdate(ProductModel.TierPriceModel model)
        {
            var tierPrice = await _db.TierPrices.FindByIdAsync(model.Id);

            tierPrice.StoreId = model.StoreId ?? 0;
            tierPrice.CustomerRoleId = model.CustomerRoleId;
            tierPrice.Quantity = model.Quantity;
            tierPrice.Price = model.Price1 ?? 0;
            tierPrice.CalculationMethod = (TierPriceCalculationMethod)model.CalculationMethodId;

            try
            {
                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTierPrice)]
        public async Task<IActionResult> TierPriceDelete(GridSelection selection)
        {
            var ids = selection.GetEntityIds();
            var numDeleted = 0;

            if (ids.Any())
            {
                var toDelete = await _db.TierPrices.GetManyAsync(ids);
                _db.TierPrices.RemoveRange(toDelete);
                numDeleted = await _db.SaveChangesAsync();
            }

            return Json(new { Success = true, Count = numDeleted });
        }

        #endregion

        #region Downloads

        [HttpPost]
        [Permission(Permissions.Media.Download.Delete)]
        public async Task<IActionResult> DeleteDownloadVersion(int downloadId)
        {
            var download = await _db.Downloads.FindByIdAsync(downloadId);
            if (download == null)
                return NotFound();

            _db.Downloads.Remove(download);
            await _db.SaveChangesAsync();
            
            return Json( new { success = true, Message = T("Admin.Common.TaskSuccessfullyProcessed").Value } );
        }

        [NonAction]
        protected async Task UpdateDataOfProductDownloadsAsync(ProductModel model)
        {
            var testVersions = (new [] { model.DownloadFileVersion, model.NewVersion }).Where(x => x.HasValue());
            var saved = false;
            foreach (var testVersion in testVersions)
            {
                try
                {
                    var test = SemanticVersion.Parse(testVersion);

                    // Insert versioned downloads here so they won't be saved if version ain't correct.
                    // If NewVersionDownloadId has value
                    if (model.NewVersion.HasValue() && !saved)
                    {
                        await InsertProductDownloadAsync(model.NewVersionDownloadId, model.Id, model.NewVersion);
                        saved = true;
                    }
                    else
                    {
                        await InsertProductDownloadAsync(model.DownloadId, model.Id, model.DownloadFileVersion);
                    }
                }
                catch
                {
                    ModelState.AddModelError("DownloadFileVersion", T("Admin.Catalog.Products.Download.SemanticVersion.NotValid"));
                }
            }

            var isUrlDownload = Request.Form["is-url-download-" + model.SampleDownloadId] == "true";
            var setOldFileToTransient = false;

            if (model.SampleDownloadId != model.OldSampleDownloadId && model.SampleDownloadId != 0 && !isUrlDownload)
            {
                // Insert sample download if a new file was uploaded.
                model.SampleDownloadId = await InsertSampleDownloadAsync(model.SampleDownloadId, model.Id);

                setOldFileToTransient = true;
            }
            else if (isUrlDownload)
            {
                var download = await _db.Downloads.FindByIdAsync((int)model.SampleDownloadId);
                download.IsTransient = false;
                await _db.SaveChangesAsync();

                setOldFileToTransient = true;
            }

            if (setOldFileToTransient && model.OldSampleDownloadId > 0)
            {
                var download = await _db.Downloads.FindByIdAsync((int)model.OldSampleDownloadId);
                download.IsTransient = true;
                await _db.SaveChangesAsync();
            }
        }

        [NonAction]
        protected async Task InsertProductDownloadAsync(int? fileId, int entityId, string fileVersion = "")
        {
            if (fileId > 0)
            {
                var isUrlDownload = Request.Form["is-url-download-" + fileId] == "true";

                if (!isUrlDownload)
                {
                    var mediaFileInfo = await _mediaService.GetFileByIdAsync((int)fileId);
                    var download = new Download
                    {
                        MediaFile = mediaFileInfo.File,
                        EntityId = entityId,
                        EntityName = nameof(Product),
                        DownloadGuid = Guid.NewGuid(),
                        UseDownloadUrl = false,
                        DownloadUrl = string.Empty,
                        UpdatedOnUtc = DateTime.UtcNow,
                        IsTransient = false,
                        FileVersion = fileVersion
                    };

                    _db.Downloads.Add(download);
                    await _db.SaveChangesAsync();
                }
                else
                {
                    var download = await _db.Downloads.FindByIdAsync((int)fileId);
                    download.FileVersion = fileVersion;
                    download.IsTransient = false;
                    await _db.SaveChangesAsync();
                }
            }
        }

        [NonAction]
        protected async Task<int?> InsertSampleDownloadAsync(int? fileId, int entityId)
        {
            if (fileId > 0)
            {
                var mediaFileInfo = await _mediaService.GetFileByIdAsync((int)fileId);
                var download = new Download
                {
                    MediaFile = mediaFileInfo.File,
                    EntityId = entityId,
                    EntityName = nameof(Product),
                    DownloadGuid = Guid.NewGuid(),
                    UseDownloadUrl = false,
                    DownloadUrl = string.Empty,
                    UpdatedOnUtc = DateTime.UtcNow,
                    IsTransient = false
                };

                _db.Downloads.Add(download);
                await _db.SaveChangesAsync();

                return download.Id;
            }

            return null;
        }

        #endregion

        #region Product tags

        [Permission(Permissions.Catalog.Product.Read)]
        public IActionResult ProductTags()
        {
            return View();
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> ProductTagsList(GridCommand command)
        {
            var model = new GridModel<ProductTagModel>();
            var tags = await _db.ProductTags
                .AsNoTracking()
                .ApplyGridCommand(command, false)
                .ToPagedList(command)
                .LoadAsync();

            model.Rows = await tags
                .AsQueryable()
                .SelectAsync(async x =>
                {
                    return new ProductTagModel
                    {
                        Id = x.Id,
                        Name = x.Name,
                        Published = x.Published,
                        ProductCount = await _productTagService.CountProductsByTagIdAsync(x.Id)
                    };
                }).AsyncToList();
            
            model.Total = await tags.GetTotalCountAsync();

            return Json(model);
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTag)]
        public async Task<IActionResult> ProductTagsDelete(GridSelection selection)
        {
            var ids = selection.GetEntityIds();
            var numDeleted = 0;

            if (ids.Any())
            {
                var toDelete = await _db.ProductTags.GetManyAsync(ids);
                _db.ProductTags.RemoveRange(toDelete);
                numDeleted = await _db.SaveChangesAsync();
            }

            return Json(new { Success = true, Count = numDeleted });
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTag)]
        public async Task<IActionResult> ProductTagsUpdate(ProductTagModel model)
        {
            var productTag = await _db.ProductTags.FindByIdAsync(model.Id);

            try
            {
                productTag.Published = model.Published;
                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                NotifyError(ex.GetInnerMessage());
                return Json(new { success = false });
            }
        }

        [Permission(Permissions.Catalog.Product.EditTag)]
        public async Task<IActionResult> EditProductTag(string btnId, string formId, int id)
        {
            var productTag = await _db.ProductTags
                .Include(x => x.Products)
                .FindByIdAsync(id, false);
                
            if (productTag == null)
            {
                return NotFound();
            }

            var model = new ProductTagModel
            {
                Id = productTag.Id,
                Name = productTag.Name,
                Published = productTag.Published,
                ProductCount = productTag.Products.Count
            };

            AddLocales(model.Locales, (locale, languageId) =>
            {
                locale.Name = productTag.GetLocalized(x => x.Name, languageId, false, false);
            });

            ViewBag.btnId = btnId;
            ViewBag.formId = formId;

            return View(model);
        }

        [HttpPost]
        [Permission(Permissions.Catalog.Product.EditTag)]
        public async Task<IActionResult> EditProductTag(string btnId, string formId, ProductTagModel model)
        {
            var productTag = await _db.ProductTags
                .Include(x => x.Products)
                .FindByIdAsync(model.Id);

            if (productTag == null)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                productTag.Name = model.Name;
                productTag.Published = model.Published;

                await UpdateLocalesAsync(productTag, model);
                await _db.SaveChangesAsync();

                ViewBag.RefreshPage = true;
                ViewBag.btnId = btnId;
                ViewBag.formId = formId;
            }

            return View(model);
        }

        [NonAction]
        private async Task UpdateLocalesAsync(ProductTag productTag, ProductTagModel model)
        {
            foreach (var localized in model.Locales)
            {
                await _localizedEntityService.ApplyLocalizedValueAsync(productTag, x => x.Name, localized.Name, localized.LanguageId);
            }
        }

        #endregion

        #region Low stock reports

        [Permission(Permissions.Catalog.Product.Read)]
        public IActionResult LowStockReport()
        {
            return View();
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        [Permission(Permissions.Catalog.Product.Read)]
        public async Task<IActionResult> LowStockReportList(GridCommand command)
        {
            var model = new GridModel<ProductModel>();
            var allProducts = await _productService.GetLowStockProducts()
                .ApplyGridCommand(command)
                .ToPagedList(command)
                .LoadAsync();

            model.Rows = await allProducts.SelectAsync(async x =>
            {
                var productModel = await MapperFactory.MapAsync<Product, ProductModel>(x);
                productModel.ProductTypeName = x.GetProductTypeLabel(Services.Localization);
                productModel.EditUrl = Url.Action("Edit", "Product", new { id = x.Id });
                return productModel;
            }).AsyncToList();

            model.Total = await allProducts.GetTotalCountAsync();

            return Json(model);
        }

        #endregion

        #region Hidden normalizers

        [Permission(Permissions.Catalog.Product.Update)]
        public async Task<IActionResult> FixProductMainPictureIds(DateTime? ifModifiedSinceUtc = null)
        {
            var count = await ProductPictureHelper.FixProductMainPictureIds(_db, ifModifiedSinceUtc);

            return Content("Fixed {0} ids.".FormatInvariant(count));
        }

        #endregion

        #region Utitilies

        private async Task PrepareProductModelAsync(ProductModel model, Product product, bool setPredefinedValues, bool excludeProperties)
        {
            Guard.NotNull(model, nameof(model));

            if (product != null)
            {
                var parentGroupedProduct = await _db.Products.FindByIdAsync(product.ParentGroupedProductId, false);
                if (parentGroupedProduct != null)
                {
                    model.AssociatedToProductId = product.ParentGroupedProductId;
                    model.AssociatedToProductName = parentGroupedProduct.Name;
                }

                model.CreatedOn = _dateTimeHelper.ConvertToUserTime(product.CreatedOnUtc, DateTimeKind.Utc);
                model.UpdatedOn = _dateTimeHelper.ConvertToUserTime(product.UpdatedOnUtc, DateTimeKind.Utc);
                model.SelectedStoreIds = await _storeMappingService.GetAuthorizedStoreIdsAsync(product);
                model.SelectedCustomerRoleIds = await _aclService.GetAuthorizedCustomerRoleIdsAsync(product);
                model.OriginalStockQuantity = product.StockQuantity;

                if (product.LimitedToStores)
                {
                    var storeMappings = await _storeMappingService.GetStoreMappingCollectionAsync(nameof(Product), new[] { product.Id });
                    var currentStoreId = Services.StoreContext.CurrentStore.Id;

                    if (storeMappings.FirstOrDefault(x => x.StoreId == currentStoreId) == null)
                    {
                        var storeMapping = storeMappings.FirstOrDefault();
                        if (storeMapping != null)
                        {
                            var store = Services.StoreContext.GetStoreById(storeMapping.StoreId);
                            if (store != null)
                                model.ProductUrl = store.Url.EnsureEndsWith("/") + await product.GetActiveSlugAsync();
                        }
                    }
                }

                if (model.ProductUrl.IsEmpty())
                {
                    model.ProductUrl = Url.RouteUrl("Product", new { SeName = await product.GetActiveSlugAsync() }, Request.Scheme);
                }

                // Downloads.
                var productDownloads = await _db.Downloads
                    .AsNoTracking()
                    .Include(x => x.MediaFile)
                    .ApplyEntityFilter(product)
                    .ApplyVersionFilter(string.Empty)
                    .ToListAsync();

                var idsOrderedByVersion = productDownloads
                    .Select(x => new { x.Id, Version = SemanticVersion.Parse(x.FileVersion.HasValue() ? x.FileVersion : "1.0.0.0") })
                    .OrderByDescending(x => x.Version)
                    .Select(x => x.Id);

                productDownloads = productDownloads.OrderBySequence(idsOrderedByVersion).ToList();

                model.DownloadVersions = productDownloads
                    .Select(x => new DownloadVersion
                    {
                        FileVersion = x.FileVersion,
                        DownloadId = x.Id,
                        FileName = x.UseDownloadUrl ? x.DownloadUrl : x.MediaFile?.Name,
                        DownloadUrl = x.UseDownloadUrl ? x.DownloadUrl : Url.Action("DownloadFile", "Download", new { downloadId = x.Id })
                    })
                    .ToList();

                var currentDownload = productDownloads.FirstOrDefault();

                model.DownloadId = currentDownload?.Id;
                model.CurrentDownload = currentDownload;
                if (currentDownload?.MediaFile != null)
                {
                    model.DownloadThumbUrl = await _mediaService.GetUrlAsync(currentDownload.MediaFile.Id, _mediaSettings.CartThumbPictureSize, null, true);
                    currentDownload.DownloadUrl = Url.Action("DownloadFile", "Download", new { downloadId = currentDownload.Id });
                    model.CurrentFile = await _mediaService.GetFileByIdAsync(currentDownload.MediaFile.Id);
                }

                model.DownloadFileVersion = (currentDownload?.FileVersion).EmptyNull();
                model.OldSampleDownloadId = model.SampleDownloadId;

                // Media files.
                var file = await _mediaService.GetFileByIdAsync(product.MainPictureId ?? 0);
                model.PictureThumbnailUrl = _mediaService.GetUrl(file, _mediaSettings.CartThumbPictureSize);
                model.NoThumb = file == null;

                await PrepareProductPictureModelAsync(model);
                model.AddPictureModel.PictureId = product.MainPictureId ?? 0;
            }

            model.PrimaryStoreCurrencyCode = Services.StoreContext.CurrentStore.PrimaryStoreCurrency.CurrencyCode;

            var measure = await _db.MeasureWeights.FindByIdAsync(_measureSettings.BaseWeightId, false);
            var dimension = await _db.MeasureDimensions.FindByIdAsync(_measureSettings.BaseDimensionId, false);

            model.BaseWeightIn = measure?.GetLocalized(x => x.Name) ?? string.Empty;
            model.BaseDimensionIn = dimension?.GetLocalized(x => x.Name) ?? string.Empty;

            model.NumberOfAvailableProductAttributes = await _db.ProductAttributes.CountAsync();
            model.NumberOfAvailableManufacturers = await _db.Manufacturers.CountAsync();
            model.NumberOfAvailableCategories = await _db.Categories.CountAsync();

            // Copy product.
            if (product != null)
            {
                model.CopyProductModel.Id = product.Id;
                model.CopyProductModel.Name = T("Admin.Common.CopyOf", product.Name);
                model.CopyProductModel.Published = true;
            }

            // Templates.
            var templates = await _db.ProductTemplates
                .AsNoTracking()
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync();

            ViewBag.AvailableProductTemplates = new List<SelectListItem>();
            foreach (var template in templates)
            {
                ViewBag.AvailableProductTemplates.Add(new SelectListItem
                {
                    Text = template.Name,
                    Value = template.Id.ToString()
                });
            }

            // Product tags.
            if (product != null)
            {
                model.ProductTags = product.ProductTags.Select(x => x.Name).ToArray();
            }

            var allTags = await _db.ProductTags
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => x.Name)
                .ToListAsync();

            ViewBag.AvailableProductTags = new MultiSelectList(allTags, model.ProductTags);

            // Tax categories.
            var taxCategories = await _db.TaxCategories
                .AsNoTracking()
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync();

            ViewBag.AvailableTaxCategories = new List<SelectListItem>();
            foreach (var tc in taxCategories)
            {
                ViewBag.AvailableTaxCategories.Add(new SelectListItem
                {
                    Text = tc.Name,
                    Value = tc.Id.ToString(),
                    Selected = product != null && !setPredefinedValues && tc.Id == product.TaxCategoryId
                });
            }

            // Do not pre-select a tax category that is not stored.
            if (product != null && product.TaxCategoryId == 0)
            {
                ViewBag.AvailableTaxCategories.Insert(0, new SelectListItem { Text = T("Common.PleaseSelect"), Value = string.Empty, Selected = true });
            }

            // Delivery times.
            if (setPredefinedValues)
            {
                var defaultDeliveryTime = await _db.DeliveryTimes
                    .AsNoTracking()
                    .Where(x => x.IsDefault == true)
                    .FirstOrDefaultAsync();

                model.DeliveryTimeId = defaultDeliveryTime?.Id;
            }

            // Quantity units.
            var quantityUnits = await _db.QuantityUnits
                .AsNoTracking()
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync();

            ViewBag.AvailableQuantityUnits = new List<SelectListItem>();
            foreach (var mu in quantityUnits)
            {
                ViewBag.AvailableQuantityUnits.Add(new SelectListItem
                {
                    Text = mu.Name,
                    Value = mu.Id.ToString(),
                    Selected = product != null && !setPredefinedValues && mu.Id == product.QuantityUnitId.GetValueOrDefault()
                });
            }

            // BasePrice aka PAnGV
            var measureUnitKeys = await _db.MeasureWeights.AsNoTracking().OrderBy(x => x.DisplayOrder).Select(x => x.SystemKeyword).ToListAsync();
            var measureDimensionKeys = await _db.MeasureDimensions.AsNoTracking().OrderBy(x => x.DisplayOrder).Select(x => x.SystemKeyword).ToListAsync();
            var measureUnits = new HashSet<string>(measureUnitKeys.Concat(measureDimensionKeys), StringComparer.OrdinalIgnoreCase);

            // Don't forget biz import!
            if (product != null && !setPredefinedValues && product.BasePriceMeasureUnit.HasValue())
            {
                measureUnits.Add(product.BasePriceMeasureUnit);
            }

            ViewBag.AvailableMeasureUnits = new List<SelectListItem>();
            foreach (var mu in measureUnits)
            {
                ViewBag.AvailableMeasureUnits.Add(new SelectListItem
                {
                    Text = mu,
                    Value = mu,
                    Selected = product != null && !setPredefinedValues && mu.EqualsNoCase(product.BasePriceMeasureUnit)
                });
            }

            // Specification attributes.
            // TODO: (mh) (core) We can't do this!!! The list can be very large. This needs to be AJAXified. TBD with MC.
            var specificationAttributes = await _db.SpecificationAttributes
                .AsNoTracking()
                .OrderBy(x => x.DisplayOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            var availableAttributes = new List<SelectListItem>();
            var availableOptions = new List<SelectListItem>();
            for (int i = 0; i < specificationAttributes.Count; i++)
            {
                var sa = specificationAttributes[i];
                availableAttributes.Add(new SelectListItem { Text = sa.Name, Value = sa.Id.ToString() });
                if (i == 0)
                {
                    var options = await _db.SpecificationAttributeOptions
                        .AsNoTracking()
                        .Where(x => x.SpecificationAttributeId == sa.Id)
                        .OrderBy(x => x.DisplayOrder)
                        .ToListAsync();

                    // Attribute options.
                    foreach (var sao in options)
                    {
                        availableOptions.Add(new SelectListItem { Text = sao.Name, Value = sao.Id.ToString() });
                    }
                }
            }

            ViewBag.AvailableAttributes = availableAttributes;
            ViewBag.AvailableOptions = availableOptions;

            if (product != null && !excludeProperties)
            {
                model.SelectedDiscountIds = product.AppliedDiscounts.Select(d => d.Id).ToArray();
            }

            var inventoryMethods = ((ManageInventoryMethod[])Enum.GetValues(typeof(ManageInventoryMethod))).Where(
                x => model.ProductTypeId != (int)ProductType.BundledProduct || x != ManageInventoryMethod.ManageStockByAttributes
            );

            ViewBag.AvailableManageInventoryMethods = new List<SelectListItem>();
            foreach (var inventoryMethod in inventoryMethods)
            {
                ViewBag.AvailableManageInventoryMethods.Add(new SelectListItem
                {
                    Value = ((int)inventoryMethod).ToString(),
                    Text = inventoryMethod.GetLocalizedEnum(),
                    Selected = ((int)inventoryMethod == model.ManageInventoryMethodId)
                });
            }

            ViewBag.AvailableCountries = await _db.Countries.AsNoTracking().ApplyStandardFilter(true)
                .Select(x => new SelectListItem
                {
                    Text = x.Name,
                    Value = x.Id.ToString(),
                    Selected = product != null && x.Id == product.CountryOfOriginId
                })
                .ToListAsync();

            if (setPredefinedValues)
            {
                // TODO: These should be hidden settings.
                model.MaximumCustomerEnteredPrice = 1000;
                model.MaxNumberOfDownloads = 10;
                model.RecurringCycleLength = 100;
                model.RecurringTotalCycles = 10;
                model.StockQuantity = 10000;
                model.NotifyAdminForQuantityBelow = 1;
                model.OrderMinimumQuantity = 1;
                model.OrderMaximumQuantity = 100;
                model.QuantityStep = 1;
                model.HideQuantityControl = false;
                model.UnlimitedDownloads = true;
                model.IsShippingEnabled = true;
                model.AllowCustomerReviews = true;
                model.Published = true;
                model.HasPreviewPicture = false;
            }
        }

        private async Task PrepareProductPictureModelAsync(ProductModel model)
        {
            Guard.NotNull(model, nameof(model));

            var productPictures = await _db.ProductMediaFiles
                .AsNoTracking()
                .Include(x => x.MediaFile)
                .ApplyProductFilter(model.Id)
                .ToListAsync();

            model.ProductMediaFiles = productPictures
                .Select(x =>
                {
                    var media = new ProductMediaFile
                    {
                        Id = x.Id,
                        ProductId = x.ProductId,
                        MediaFileId = x.MediaFileId,
                        DisplayOrder = x.DisplayOrder,
                        MediaFile = x.MediaFile
                    };

                    return media;
                })
                .ToList();
        }

        private async Task PrepareBundleItemEditModelAsync(ProductBundleItemModel model, ProductBundleItem bundleItem, string btnId, string formId, bool refreshPage = false)
        {
            ViewBag.BtnId = btnId;
            ViewBag.FormId = formId;
            ViewBag.RefreshPage = refreshPage;

            if (bundleItem == null)
            {
                ViewBag.Title = T("Admin.Catalog.Products.BundleItems.EditOf").Value;
                return;
            }

            model.CreatedOn = _dateTimeHelper.ConvertToUserTime(bundleItem.CreatedOnUtc, DateTimeKind.Utc);
            model.UpdatedOn = _dateTimeHelper.ConvertToUserTime(bundleItem.UpdatedOnUtc, DateTimeKind.Utc);
            model.IsPerItemPricing = bundleItem.BundleProduct.BundlePerItemPricing;

            if (model.Locales.Count == 0)
            {
                AddLocales(model.Locales, (locale, languageId) =>
                {
                    locale.Name = bundleItem.GetLocalized(x => x.Name, languageId, false, false);
                    locale.ShortDescription = bundleItem.GetLocalized(x => x.ShortDescription, languageId, false, false);
                });
            }

            ViewBag.Title = $"{T("Admin.Catalog.Products.BundleItems.EditOf")} {bundleItem.Product.Name} ({bundleItem.Product.Sku})";

            var attributes = await _db.ProductVariantAttributes
                .AsNoTracking()
                .Include(x => x.ProductAttribute)
                .ApplyProductFilter(new[] { bundleItem.ProductId })
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync();

            foreach (var attribute in attributes)
            {
                var attributeModel = new ProductBundleItemAttributeModel()
                {
                    Id = attribute.Id,
                    Name = attribute.ProductAttribute.Alias.HasValue() ? $"{attribute.ProductAttribute.Name} ({attribute.ProductAttribute.Alias})" : attribute.ProductAttribute.Name
                };

                var attributeValues = await _db.ProductVariantAttributeValues
                    .AsNoTracking()
                    .OrderBy(x => x.DisplayOrder)
                    .Where(x => x.ProductVariantAttributeId == attribute.Id)
                    .ToListAsync();

                foreach (var attributeValue in attributeValues)
                {
                    var filteredValue = bundleItem.AttributeFilters.FirstOrDefault(x => x.AttributeId == attribute.Id && x.AttributeValueId == attributeValue.Id);

                    attributeModel.Values.Add(new SelectListItem()
                    {
                        Text = attributeValue.Name,
                        Value = attributeValue.Id.ToString(),
                        Selected = (filteredValue != null)
                    });

                    if (filteredValue != null)
                    {
                        attributeModel.PreSelect.Add(new SelectListItem()
                        {
                            Text = attributeValue.Name,
                            Value = attributeValue.Id.ToString(),
                            Selected = filteredValue.IsPreSelected
                        });
                    }
                }

                if (attributeModel.Values.Count > 0)
                {
                    if (attributeModel.PreSelect.Count > 0)
                    {
                        attributeModel.PreSelect.Insert(0, new SelectListItem() { Text = T("Admin.Common.PleaseSelect") });
                    }

                    model.Attributes.Add(attributeModel);
                }
            }
        }

        private async Task SaveFilteredAttributesAsync(ProductBundleItem bundleItem)
        {
            var form = Request.Form;

            var toDelete = await _db.ProductBundleItemAttributeFilter
                .Where(x => x.BundleItemId == bundleItem.Id)
                .ToListAsync();

            _db.ProductBundleItemAttributeFilter.RemoveRange(toDelete);
            await _db.SaveChangesAsync();

            var allFilterKeys = form.Keys.Where(x => x.HasValue() && x.StartsWith(ProductBundleItemAttributeModel.AttributeControlPrefix));

            foreach (var key in allFilterKeys)
            {
                int attributeId = key[ProductBundleItemAttributeModel.AttributeControlPrefix.Length..].ToInt();
                string preSelectId = form[ProductBundleItemAttributeModel.PreSelectControlPrefix + attributeId.ToString()].ToString().EmptyNull();

                foreach (var valueId in form[key].ToString().SplitSafe(','))
                {
                    var attributeFilter = new ProductBundleItemAttributeFilter
                    {
                        BundleItemId = bundleItem.Id,
                        AttributeId = attributeId,
                        AttributeValueId = valueId.ToInt(),
                        IsPreSelected = (preSelectId == valueId)
                    };

                    _db.ProductBundleItemAttributeFilter.Add(attributeFilter);
                }

                await _db.SaveChangesAsync();
            }
        }

        #endregion

        #region Update[...]

        protected async Task MapModelToProductAsync(ProductModel model, Product product, IFormCollection form)
        {
            if (model.LoadedTabs == null || model.LoadedTabs.Length == 0)
            {
                model.LoadedTabs = new string[] { "Info" };
            }

            foreach (var tab in model.LoadedTabs)
            {
                switch (tab.ToLowerInvariant())
                {
                    case "info":
                        UpdateProductGeneralInfo(product, model);
                        break;
                    case "inventory":
                        await UpdateProductInventoryAsync(product, model);
                        break;
                    case "bundleitems":
                        await UpdateProductBundleItemsAsync(product, model);
                        break;
                    case "price":
                        await UpdateProductPriceAsync(product, model);
                        break;
                    case "attributes":
                        UpdateProductAttributes(product, model);
                        break;
                    case "downloads":
                        await UpdateProductDownloadsAsync(product, model);
                        break;
                    case "pictures":
                        UpdateProductPictures(product, model);
                        break;
                    case "seo":
                        await UpdateProductSeoAsync(product, model);
                        break;
                }
            }

            await Services.EventPublisher.PublishAsync(new ModelBoundEvent(model, product, form));
        }

        protected void UpdateProductGeneralInfo(Product product, ProductModel model)
        {
            var p = product;
            var m = model;

            p.ProductTypeId = m.ProductTypeId;
            p.Visibility = m.Visibility;
            p.Condition = m.Condition;
            p.ProductTemplateId = m.ProductTemplateId;
            p.Name = m.Name;
            p.ShortDescription = m.ShortDescription;
            p.FullDescription = m.FullDescription;
            p.Sku = m.Sku;
            p.ManufacturerPartNumber = m.ManufacturerPartNumber;
            p.Gtin = m.Gtin;
            p.AdminComment = m.AdminComment;
            p.AvailableStartDateTimeUtc = m.AvailableStartDateTimeUtc;
            p.AvailableEndDateTimeUtc = m.AvailableEndDateTimeUtc;

            p.AllowCustomerReviews = m.AllowCustomerReviews;
            p.ShowOnHomePage = m.ShowOnHomePage;
            p.HomePageDisplayOrder = m.HomePageDisplayOrder;
            p.Published = m.Published;
            p.RequireOtherProducts = m.RequireOtherProducts;
            p.RequiredProductIds = m.RequiredProductIds;
            p.AutomaticallyAddRequiredProducts = m.AutomaticallyAddRequiredProducts;

            p.IsGiftCard = m.IsGiftCard;
            p.GiftCardTypeId = m.GiftCardTypeId;

            p.IsRecurring = m.IsRecurring;
            p.RecurringCycleLength = m.RecurringCycleLength;
            p.RecurringCyclePeriodId = m.RecurringCyclePeriodId;
            p.RecurringTotalCycles = m.RecurringTotalCycles;

            p.IsShippingEnabled = m.IsShippingEnabled;
            p.DeliveryTimeId = m.DeliveryTimeId == 0 ? null : m.DeliveryTimeId;
            p.QuantityUnitId = m.QuantityUnitId == 0 ? null : m.QuantityUnitId;
            p.IsFreeShipping = m.IsFreeShipping;
            p.AdditionalShippingCharge = m.AdditionalShippingCharge ?? 0;
            p.Weight = m.Weight ?? 0;
            p.Length = m.Length ?? 0;
            p.Width = m.Width ?? 0;
            p.Height = m.Height ?? 0;

            p.IsEsd = m.IsEsd;
            p.IsTaxExempt = m.IsTaxExempt;
            p.TaxCategoryId = m.TaxCategoryId ?? 0;
            p.CustomsTariffNumber = m.CustomsTariffNumber;
            p.CountryOfOriginId = m.CountryOfOriginId == 0 ? null : m.CountryOfOriginId;

            p.AvailableEndDateTimeUtc = p.AvailableEndDateTimeUtc.ToEndOfTheDay();
            p.SpecialPriceEndDateTimeUtc = p.SpecialPriceEndDateTimeUtc.ToEndOfTheDay();
        }

        protected async Task UpdateProductDownloadsAsync(Product product, ProductModel model)
        {
            if (!await Services.Permissions.AuthorizeAsync(Permissions.Media.Download.Update))
            {
                return;
            }

            var p = product;
            var m = model;

            p.IsDownload = m.IsDownload;
            //p.DownloadId = m.DownloadId ?? 0;
            p.UnlimitedDownloads = m.UnlimitedDownloads;
            p.MaxNumberOfDownloads = m.MaxNumberOfDownloads;
            p.DownloadExpirationDays = m.DownloadExpirationDays;
            p.DownloadActivationTypeId = m.DownloadActivationTypeId;
            p.HasUserAgreement = m.HasUserAgreement;
            p.UserAgreementText = m.UserAgreementText;
            p.HasSampleDownload = m.HasSampleDownload;
            p.SampleDownloadId = m.SampleDownloadId == 0 ? null : m.SampleDownloadId;
        }

        protected async Task UpdateProductInventoryAsync(Product product, ProductModel model)
        {
            var p = product;
            var m = model;
            var updateStockQuantity = true;
            var stockQuantityInDatabase = product.StockQuantity;

            if (p.ManageInventoryMethod == ManageInventoryMethod.ManageStock && p.Id != 0)
            {
                if (m.OriginalStockQuantity != stockQuantityInDatabase)
                {
                    // The stock has changed since the edit page was loaded, e.g. because an order has been placed.
                    updateStockQuantity = false;

                    if (m.StockQuantity != m.OriginalStockQuantity)
                    {
                        // The merchant has changed the stock quantity manually.
                        NotifyWarning(T("Admin.Catalog.Products.StockQuantityNotChanged", stockQuantityInDatabase.ToString("N0")));
                    }
                }
            }

            if (updateStockQuantity)
            {
                p.StockQuantity = m.StockQuantity;
            }

            p.ManageInventoryMethodId = m.ManageInventoryMethodId;
            p.DisplayStockAvailability = m.DisplayStockAvailability;
            p.DisplayStockQuantity = m.DisplayStockQuantity;
            p.MinStockQuantity = m.MinStockQuantity;
            p.LowStockActivityId = m.LowStockActivityId;
            p.NotifyAdminForQuantityBelow = m.NotifyAdminForQuantityBelow;
            p.BackorderModeId = m.BackorderModeId;
            p.AllowBackInStockSubscriptions = m.AllowBackInStockSubscriptions;
            p.OrderMinimumQuantity = m.OrderMinimumQuantity;
            p.OrderMaximumQuantity = m.OrderMaximumQuantity;
            p.QuantityStep = m.QuantityStep;
            p.HideQuantityControl = m.HideQuantityControl;

            if (p.ManageInventoryMethod == ManageInventoryMethod.ManageStock && updateStockQuantity)
            {
                // Back in stock notifications.
                if (p.BackorderMode == BackorderMode.NoBackorders &&
                    p.AllowBackInStockSubscriptions &&
                    p.StockQuantity > 0 &&
                    stockQuantityInDatabase <= 0 &&
                    p.Published &&
                    !p.Deleted &&
                    !p.IsSystemProduct)
                {
                    await _stockSubscriptionService.Value.SendNotificationsToSubscribersAsync(p);
                }

                if (p.StockQuantity != stockQuantityInDatabase)
                {
                    await _productService.AdjustInventoryAsync(p, null, true, 0);
                }
            }
        }

        protected async Task UpdateProductBundleItemsAsync(Product product, ProductModel model)
        {
            var p = product;
            var m = model;

            p.BundleTitleText = m.BundleTitleText;
            p.BundlePerItemPricing = m.BundlePerItemPricing;
            p.BundlePerItemShipping = m.BundlePerItemShipping;
            p.BundlePerItemShoppingCart = m.BundlePerItemShoppingCart;

            // SEO
            foreach (var localized in model.Locales)
            {
                await _localizedEntityService.ApplyLocalizedValueAsync(product, x => x.BundleTitleText, localized.BundleTitleText, localized.LanguageId);
            }
        }

        protected async Task UpdateProductPriceAsync(Product product, ProductModel model)
        {
            var p = product;
            var m = model;

            p.Price = m.Price;
            p.OldPrice = m.OldPrice ?? 0;
            p.ProductCost = m.ProductCost ?? 0;
            p.SpecialPrice = m.SpecialPrice;
            p.SpecialPriceStartDateTimeUtc = m.SpecialPriceStartDateTimeUtc;
            p.SpecialPriceEndDateTimeUtc = m.SpecialPriceEndDateTimeUtc;
            p.DisableBuyButton = m.DisableBuyButton;
            p.DisableWishlistButton = m.DisableWishlistButton;
            p.AvailableForPreOrder = m.AvailableForPreOrder;
            p.CallForPrice = m.CallForPrice;
            p.CustomerEntersPrice = m.CustomerEntersPrice;
            p.MinimumCustomerEnteredPrice = m.MinimumCustomerEnteredPrice ?? 0;
            p.MaximumCustomerEnteredPrice = m.MaximumCustomerEnteredPrice ?? 0;

            p.BasePriceEnabled = m.BasePriceEnabled;
            p.BasePriceBaseAmount = m.BasePriceBaseAmount;
            p.BasePriceAmount = m.BasePriceAmount;
            p.BasePriceMeasureUnit = m.BasePriceMeasureUnit;

            // Discounts.
            await _discountService.ApplyDiscountsAsync(product, model.SelectedDiscountIds, DiscountType.AssignedToSkus);
        }

        protected void UpdateProductAttributes(Product product, ProductModel model)
        {
            product.AttributeChoiceBehaviour = model.AttributeChoiceBehaviour;
        }

        protected async Task UpdateProductSeoAsync(Product product, ProductModel model)
        {
            var p = product;
            var m = model;

            p.MetaKeywords = m.MetaKeywords;
            p.MetaDescription = m.MetaDescription;
            p.MetaTitle = m.MetaTitle;

            var service = _localizedEntityService;
            foreach (var localized in model.Locales)
            {
                await service.ApplyLocalizedValueAsync(product, x => x.MetaKeywords, localized.MetaKeywords, localized.LanguageId);
                await service.ApplyLocalizedValueAsync(product, x => x.MetaDescription, localized.MetaDescription, localized.LanguageId);
                await service.ApplyLocalizedValueAsync(product, x => x.MetaTitle, localized.MetaTitle, localized.LanguageId);
            }
        }

        protected void UpdateProductPictures(Product product, ProductModel model)
        {
            product.HasPreviewPicture = model.HasPreviewPicture;
        }

        private async Task UpdateDataOfExistingProductAsync(Product product, ProductModel model, bool editMode)
        {
            var p = product;
            var m = model;

            //var seoTabLoaded = m.LoadedTabs.Contains("SEO", StringComparer.OrdinalIgnoreCase);

            // SEO.
            var validateSlugResult = await p.ValidateSlugAsync(p.Name, true, 0);
            m.SeName = validateSlugResult.Slug;
            await _urlService.ApplySlugAsync(validateSlugResult);

            if (editMode)
            {
                _db.Products.Update(p);
                await _db.SaveChangesAsync();
            }

            foreach (var localized in model.Locales)
            {
                await _localizedEntityService.ApplyLocalizedValueAsync(product, x => x.Name, localized.Name, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(product, x => x.ShortDescription, localized.ShortDescription, localized.LanguageId);
                await _localizedEntityService.ApplyLocalizedValueAsync(product, x => x.FullDescription, localized.FullDescription, localized.LanguageId);

                validateSlugResult = await p.ValidateSlugAsync(localized.Name, false, localized.LanguageId);
                await _urlService.ApplySlugAsync(validateSlugResult);
            }

            await _productTagService.UpdateProductTagsAsync(p, m.ProductTags);

            await SaveStoreMappingsAsync(p, model.SelectedStoreIds);
            await SaveAclMappingsAsync(p, model.SelectedCustomerRoleIds);
        }

        #endregion
    }
}