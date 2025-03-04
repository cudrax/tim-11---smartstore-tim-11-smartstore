﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Smartstore.Admin.Models.Catalog;
using Smartstore.Admin.Models.Orders;
using Smartstore.Collections;
using Smartstore.ComponentModel;
using Smartstore.Core.Catalog;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Catalog.Pricing;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Catalog.Search;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Checkout.Orders.Reporting;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Common.Settings;
using Smartstore.Core.Content.Media;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Logging;
using Smartstore.Core.Messaging;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Core.Stores;
using Smartstore.Engine.Modularity;
using Smartstore.Utilities;
using Smartstore.Utilities.Html;
using Smartstore.Web.Controllers;
using Smartstore.Web.Models.Common;
using Smartstore.Web.Models.DataGrid;
using Smartstore.Web.Rendering;

namespace Smartstore.Admin.Controllers
{
    public class OrderController : AdminController
    {
        private readonly SmartDbContext _db;
        private readonly IOrderService _orderService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IProductAttributeMaterializer _productAttributeMaterializer;
        private readonly IPaymentService _paymentService;
        private readonly ICurrencyService _currencyService;
        private readonly ITaxService _taxService;
        private readonly IEncryptor _encryptor;
        private readonly ModuleManager _moduleManager;
        private readonly IMessageFactory _messageFactory;
        private readonly CatalogSettings _catalogSettings;
        private readonly TaxSettings _taxSettings;
        private readonly MeasureSettings _measureSettings;
        private readonly PdfSettings _pdfSettings;
        private readonly AddressSettings _addressSettings;
        private readonly SearchSettings _searchSettings;
        private readonly ShoppingCartSettings _shoppingCartSettings;
        private readonly MediaSettings _mediaSettings;
        private readonly AdminAreaSettings _adminAreaSettings;
        private readonly Currency _primaryCurrency;

        public OrderController(
            SmartDbContext db,
            IOrderService orderService,
            IOrderProcessingService orderProcessingService,
            IProductAttributeMaterializer productAttributeMaterializer,
            IPaymentService paymentService,
            ICurrencyService currencyService,
            ITaxService taxService,
            IEncryptor encryptor,
            ModuleManager moduleManager,
            IMessageFactory messageFactory,
            CatalogSettings catalogSettings,
            TaxSettings taxSettings,
            MeasureSettings measureSettings,
            PdfSettings pdfSettings,
            AddressSettings addressSettings,
            SearchSettings searchSettings,
            ShoppingCartSettings shoppingCartSettings,
            MediaSettings mediaSettings,
            AdminAreaSettings adminAreaSettings)
        {
            _db = db;
            _orderService = orderService;
            _orderProcessingService = orderProcessingService;
            _productAttributeMaterializer = productAttributeMaterializer;
            _paymentService = paymentService;
            _currencyService = currencyService;
            _taxService = taxService;
            _encryptor = encryptor;
            _moduleManager = moduleManager;
            _messageFactory = messageFactory;
            _catalogSettings = catalogSettings;
            _taxSettings = taxSettings;
            _measureSettings = measureSettings;
            _pdfSettings = pdfSettings;
            _addressSettings = addressSettings;
            _searchSettings = searchSettings;
            _shoppingCartSettings = shoppingCartSettings;
            _mediaSettings = mediaSettings;
            _adminAreaSettings = adminAreaSettings;

            _primaryCurrency = currencyService.PrimaryCurrency;
        }

        public IActionResult Index()
        {
            return RedirectToAction(nameof(List));
        }

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> List()
        {
            var allPaymentMethods = await _paymentService.LoadAllPaymentMethodsAsync();

            var paymentMethods = allPaymentMethods
                .Select(x => new SelectListItem
                {
                    Text = (_moduleManager.GetLocalizedFriendlyName(x.Metadata).NullEmpty() ?? x.Metadata.FriendlyName.NullEmpty() ?? x.Metadata.SystemName).EmptyNull(),
                    Value = x.Metadata.SystemName
                })
                .ToList();

            var paymentMethodsCounts = paymentMethods
                .GroupBy(x => x.Text)
                .Select(x => new { Name = x.Key.EmptyNull(), Count = x.Count() })
                .ToDictionarySafe(x => x.Name, x => x.Count);

            // Append system name if there are payment methods with the same friendly name.
            paymentMethods = paymentMethods
                .OrderBy(x => x.Text)
                .Select(x =>
                {
                    if (paymentMethodsCounts.TryGetValue(x.Text, out var count) && count > 1)
                    {
                        x.Text = $"{x.Text} ({x.Value})";
                    }

                    return x;
                })
                .ToList();

            ViewBag.PaymentMethods = paymentMethods;
            ViewBag.Stores = Services.StoreContext.GetAllStores().ToSelectListItems();
            ViewBag.HideProfitReport = false;

            return View(new OrderListModel());
        }

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> OrderList(GridCommand command, OrderListModel model)
        {
            var dtHelper = Services.DateTimeHelper;
            var viaShippingMethodString = T("Admin.Order.ViaShippingMethod").Value;
            var withPaymentMethodString = T("Admin.Order.WithPaymentMethod").Value;
            var fromStoreString = T("Admin.Order.FromStore").Value;
            var paymentMethodSystemnames = model.PaymentMethods.SplitSafe(',').ToArray();

            DateTime? startDateUtc = model.StartDate == null
                ? null
                : dtHelper.ConvertToUtcTime(model.StartDate.Value, dtHelper.CurrentTimeZone);

            DateTime? endDateUtc = model.EndDate == null
                ? null
                : dtHelper.ConvertToUtcTime(model.EndDate.Value, dtHelper.CurrentTimeZone).AddDays(1);

            // Create order query.
            var orderQuery = _db.Orders
                .Include(x => x.OrderItems)
                .IncludeBillingAddress()
                .IncludeShippingAddress()
                .AsNoTracking()
                .ApplyAuditDateFilter(startDateUtc, endDateUtc)
                .ApplyStatusFilter(model.OrderStatusIds, model.PaymentStatusIds, model.ShippingStatusIds)
                .ApplyPaymentFilter(paymentMethodSystemnames);

            if (model.CustomerEmail.HasValue())
            {
                orderQuery = orderQuery.ApplySearchFilterFor(x => x.BillingAddress.Email, model.CustomerEmail);
            }
            if (model.CustomerName.HasValue())
            {
                // InvalidOperationException: The binary operator OrElse is not defined for...
                //orderQuery = orderQuery.ApplySearchFilter(
                //    model.CustomerName,
                //    LogicalRuleOperator.Or, 
                //    x => x.BillingAddress.FirstName, 
                //    x => x.BillingAddress.LastName);

                orderQuery = orderQuery.Where(x => x.BillingAddress.LastName.Contains(model.CustomerName) || x.BillingAddress.FirstName.Contains(model.CustomerName));
            }
            if (model.OrderGuid.HasValue())
            {
                orderQuery = orderQuery.Where(x => x.OrderGuid.ToString().Contains(model.OrderGuid));
            }
            if (model.OrderNumber.HasValue())
            {
                orderQuery = orderQuery.ApplySearchFilterFor(x => x.OrderNumber, model.OrderNumber);
            }
            if (model.StoreId > 0)
            {
                orderQuery = orderQuery.Where(x => x.StoreId == model.StoreId);
            }

            orderQuery = orderQuery
                .OrderByDescending(x => x.CreatedOnUtc)
                .ApplyGridCommand(command);

            var orders = await orderQuery
                .ToPagedList(command)
                .LoadAsync();

            var paymentMethods = await orders
                .Where(x => x.PaymentMethodSystemName.HasValue())
                .Select(x => x.PaymentMethodSystemName)
                .Distinct()
                .SelectAsync(async x => await _paymentService.LoadPaymentMethodBySystemNameAsync(x))
                .AsyncToList();

            var paymentMethodsDic = paymentMethods
                .Where(x => x != null)
                .ToDictionarySafe(
                    x => x.Metadata.SystemName,
                    x => _moduleManager.GetLocalizedFriendlyName(x.Metadata), 
                    StringComparer.OrdinalIgnoreCase);

            var rows = await orders.SelectAsync(async x =>
            {
                paymentMethodsDic.TryGetValue(x.PaymentMethodSystemName, out var paymentMethod);

                var shipTo = x.ShippingAddress;
                var m = new OrderOverviewModel();

                await PrepareOrderOverviewModel(m, x);

                m.PaymentMethod = paymentMethod.NullEmpty() ?? x.PaymentMethodSystemName;
                m.ViaShippingMethod = viaShippingMethodString.FormatInvariant(m.ShippingMethod);
                m.WithPaymentMethod = withPaymentMethodString.FormatInvariant(m.PaymentMethod);
                m.FromStore = fromStoreString.FormatInvariant(m.StoreName);

                if (shipTo != null && m.IsShippable)
                {
                    m.ShippingAddressString = $"{shipTo.Address1}, {shipTo.ZipPostalCode} {shipTo.City}";
                    if (shipTo.Country != null)
                    {
                        m.ShippingAddressString += ", " + shipTo.Country.TwoLetterIsoCode;
                    }
                }

                return m;
            })
            .AsyncToList();

            var productCost = await orderQuery.GetOrdersProductCostsAsync();
            var summary = await orderQuery.SelectAsOrderAverageReportLine().FirstOrDefaultAsync() ?? new OrderAverageReportLine();
            var profit = summary.SumOrderTotal - summary.SumTax - productCost;

            return Json(new GridModel<OrderOverviewModel>
            {
                Rows = rows,
                Total = orders.TotalCount,
                Aggregates = new
                {
                    profit = _primaryCurrency.AsMoney(profit).ToString(true),
                    tax = _primaryCurrency.AsMoney(summary.SumTax).ToString(true),
                    total = _primaryCurrency.AsMoney(summary.SumOrderTotal).ToString(true)
                }
            });
        }

        [HttpPost, ActionName("List")]
        [FormValueRequired("go-to-order-by-number")]
        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> GoToOrder(OrderListModel model)
        {
            var orderId = 0;

            if (model.GoDirectlyToNumber.HasValue())
            {
                orderId = await _db.Orders
                    .Where(x => x.OrderNumber == model.GoDirectlyToNumber)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();

                if (orderId == 0 && int.TryParse(model.GoDirectlyToNumber, out orderId) && orderId > 0)
                {
                    if (!await _db.Orders.AnyAsync(x => x.Id == orderId))
                    {
                        orderId = 0;
                    }
                }
            }

            if (orderId != 0)
            {
                return RedirectToAction(nameof(Edit), new { id = orderId });
            }

            NotifyWarning(T("Admin.Order.NotFound"));

            return RedirectToAction(nameof(List));
        }

        [HttpPost]
        [Permission(Permissions.Order.Read)]
        public IActionResult ExportPdf(GridSelection selection)
        {
            var ids = string.Join(",", selection.SelectedKeys);

            return RedirectToAction("PrintMany", "Order", new { ids, pdf = true, area = string.Empty });
        }

        #region Payment

        [HttpPost]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> ProcessOrder(string selectedIds, string operation)
        {
            var ids = selectedIds.ToIntArray();
            var orders = await _db.Orders
                .IncludeCustomer(true)
                .IncludeOrderItems()
                .IncludeShipments()
                .IncludeGiftCardHistory()
                .IncludeBillingAddress()
                .Where(x => ids.Contains(x.Id))
                .ToListAsync();

            if (!orders.Any() || operation.IsEmpty())
            {
                return RedirectToAction(nameof(List));
            }

            const int maxErrors = 3;
            var success = 0;
            var skipped = 0;
            var errors = 0;
            var errorMessages = new HashSet<string>();
            var succeededOrderNumbers = new HashSet<string>();

            foreach (var o in orders)
            {
                try
                {
                    var succeeded = false;

                    switch (operation)
                    {
                        case "cancel":
                            if (o.CanCancelOrder())
                            {
                                await _orderProcessingService.CancelOrderAsync(o, true);
                                succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "complete":
                            if (o.CanCompleteOrder())
                            {
                                await _orderProcessingService.CompleteOrderAsync(o);
                                succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "markpaid":
                            if (o.CanMarkOrderAsPaid())
                            {
                                await _orderProcessingService.MarkOrderAsPaidAsync(o);
                                succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "capture":
                            if (await _orderProcessingService.CanCaptureAsync(o))
                            {
                                var captureErrors = await _orderProcessingService.CaptureAsync(o);
                                errorMessages.AddRange(captureErrors);
                                if (!captureErrors.Any())
                                    succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "refundoffline":
                            if (o.CanRefundOffline())
                            {
                                await _orderProcessingService.RefundOfflineAsync(o);
                                succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "refund":
                            if (await _orderProcessingService.CanRefundAsync(o))
                            {
                                var refundErrors = await _orderProcessingService.RefundAsync(o);
                                errorMessages.AddRange(refundErrors);
                                if (!refundErrors.Any())
                                    succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "voidoffline":
                            if (o.CanVoidOffline())
                            {
                                await _orderProcessingService.VoidOfflineAsync(o);
                                succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                        case "void":
                            if (await _orderProcessingService.CanVoidAsync(o))
                            {
                                var voidErrors = await _orderProcessingService.VoidAsync(o);
                                errorMessages.AddRange(voidErrors);
                                if (!voidErrors.Any())
                                    succeeded = true;
                            }
                            else
                            {
                                ++skipped;
                            }
                            break;
                    }

                    if (succeeded)
                    {
                        ++success;
                        succeededOrderNumbers.Add(o.GetOrderNumber());
                    }
                }
                catch (Exception ex)
                {
                    errorMessages.Add(ex.Message);
                    if (++errors <= maxErrors)
                    {
                        Logger.Error(ex);
                    }
                }
            }

            using var psb = StringBuilderPool.Instance.Get(out var msg);
            msg.Append(T("Admin.Orders.ProcessingResult", success, ids.Length, skipped, skipped == 0 ? " class='hide'" : ""));
            errorMessages.Take(maxErrors).Each(x => msg.Append($"<div class='text-danger mt-2'>{x}</div>"));

            NotifyInfo(msg.ToString());

            if (succeededOrderNumbers.Any())
            {
                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), string.Join(", ", succeededOrderNumbers.OrderBy(x => x)));
            }

            return RedirectToAction(nameof(List));
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("cancelorder")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> CancelOrder(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                await _orderProcessingService.CancelOrderAsync(order, true);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("completeorder")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> CompleteOrder(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                await _orderProcessingService.CompleteOrderAsync(order);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("captureorder")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> CaptureOrder(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                var errors = await _orderProcessingService.CaptureAsync(order);
                foreach (var error in errors)
                {
                    NotifyError(error);
                }

                if (!errors.Any())
                {
                    Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("markorderaspaid")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> MarkOrderAsPaid(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                await _orderProcessingService.MarkOrderAsPaidAsync(order);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("refundorder")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> RefundOrder(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                var errors = await _orderProcessingService.RefundAsync(order);
                foreach (var error in errors)
                {
                    NotifyError(error);
                }

                if (!errors.Any())
                {
                    Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("refundorderoffline")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> RefundOrderOffline(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                await _orderProcessingService.RefundOfflineAsync(order);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("voidorder")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> VoidOrder(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                var errors = await _orderProcessingService.VoidAsync(order);
                foreach (var error in errors)
                {
                    NotifyError(error);
                }

                if (!errors.Any())
                {
                    Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("voidorderoffline")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> VoidOrderOffline(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                await _orderProcessingService.VoidOfflineAsync(order);

                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> PartiallyRefundOrderPopup(int id, bool online)
        {
            var order = await GetOrderWithIncludes(id, false);
            if (order == null)
            {
                return NotFound();
            }

            var model = new OrderModel();
            await PrepareOrderModel(model, order);

            return View(model);
        }

        [HttpPost]
        [FormValueRequired("partialrefundorder")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> PartiallyRefundOrderPopup(string btnId, string formId, int id, bool online, OrderModel model)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            try
            {
                IList<string> errors = null;
                var amountToRefund = model.AmountToRefund;
                var maxAmountToRefund = order.OrderTotal - order.RefundedAmount;

                if (amountToRefund > maxAmountToRefund)
                {
                    amountToRefund = maxAmountToRefund;
                }

                if (amountToRefund <= decimal.Zero)
                {
                    errors = new List<string> { T("Admin.OrderNotice.RefundAmountError") };
                }
                else if (online)
                {
                    errors = await _orderProcessingService.PartiallyRefundAsync(order, amountToRefund);
                }
                else
                {
                    await _orderProcessingService.PartiallyRefundOfflineAsync(order, amountToRefund);
                }

                if (errors?.Any() ?? false)
                {
                    foreach (var error in errors)
                    {
                        NotifyError(error, false);
                    }
                }
                else
                {
                    Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

                    ViewBag.RefreshPage = true;
                    ViewBag.btnId = btnId;
                    ViewBag.formId = formId;
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex, false);
            }

            await PrepareOrderModel(model, order);

            return View(model);
        }

        #endregion

        #region Edit, delete

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> Edit(int id)
        {
            var order = await GetOrderWithIncludes(id, false);
            if (order == null)
            {
                return NotFound();
            }

            var model = new OrderModel();
            await PrepareOrderModel(model, order);

            return View(model);
        }

        [HttpPost]
        [Permission(Permissions.Order.Delete)]
        public async Task<IActionResult> Delete(int id)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            var msg = T("ActivityLog.DeleteOrder", order.GetOrderNumber());

            await _orderProcessingService.DeleteOrderAsync(order);

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.DeleteOrder, msg);
            NotifySuccess(msg);

            return RedirectToAction(nameof(List));
        }

        [Permission(Permissions.Order.Read)]
        public IActionResult Print(int orderId, bool pdf = false)
        {
            return RedirectToAction("Print", "Order", new { id = orderId, pdf, area = string.Empty });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("btnSaveCC")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> EditCreditCardInfo(int id, OrderModel model)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            if (order.AllowStoringCreditCardNumber)
            {
                order.CardType = _encryptor.EncryptText(model.CardType);
                order.CardName = _encryptor.EncryptText(model.CardName);
                order.CardNumber = _encryptor.EncryptText(model.CardNumber);
                order.MaskedCreditCardNumber = _encryptor.EncryptText(_paymentService.GetMaskedCreditCardNumber(model.CardNumber));
                order.CardCvv2 = _encryptor.EncryptText(model.CardCvv2);
                order.CardExpirationMonth = _encryptor.EncryptText(model.CardExpirationMonth);
                order.CardExpirationYear = _encryptor.EncryptText(model.CardExpirationYear);

                await _db.SaveChangesAsync();
                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }

            await PrepareOrderModel(model, order);

            return View(model);
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("btnSaveDD")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> EditDirectDebitInfo(int id, OrderModel model)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            if (order.AllowStoringDirectDebit)
            {
                order.DirectDebitAccountHolder = _encryptor.EncryptText(model.DirectDebitAccountHolder);
                order.DirectDebitAccountNumber = _encryptor.EncryptText(model.DirectDebitAccountNumber);
                order.DirectDebitBankCode = _encryptor.EncryptText(model.DirectDebitBankCode);
                order.DirectDebitBankName = _encryptor.EncryptText(model.DirectDebitBankName);
                order.DirectDebitBIC = _encryptor.EncryptText(model.DirectDebitBIC);
                order.DirectDebitCountry = _encryptor.EncryptText(model.DirectDebitCountry);
                order.DirectDebitIban = _encryptor.EncryptText(model.DirectDebitIban);

                await _db.SaveChangesAsync();
                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
            }

            await PrepareOrderModel(model, order);

            return View(model);
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired("btnSaveOrderTotals")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> EditOrderTotals(int id, OrderModel model)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            order.OrderSubtotalInclTax = model.OrderSubtotalInclTax;
            order.OrderSubtotalExclTax = model.OrderSubtotalExclTax;
            order.OrderSubTotalDiscountInclTax = model.OrderSubTotalDiscountInclTax;
            order.OrderSubTotalDiscountExclTax = model.OrderSubTotalDiscountExclTax;
            order.OrderShippingInclTax = model.OrderShippingInclTax;
            order.OrderShippingExclTax = model.OrderShippingExclTax;
            order.PaymentMethodAdditionalFeeInclTax = model.PaymentMethodAdditionalFeeInclTax;
            order.PaymentMethodAdditionalFeeExclTax = model.PaymentMethodAdditionalFeeExclTax;
            order.TaxRates = model.TaxRates;
            order.OrderTax = model.OrderTax;
            order.OrderDiscount = model.OrderDiscount;
            order.CreditBalance = model.CreditBalance;
            order.OrderTotalRounding = model.OrderTotalRounding;
            order.OrderTotal = model.OrderTotal;

            await _db.SaveChangesAsync();
            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

            await PrepareOrderModel(model, order);

            return View(model);
        }

        [HttpPost]
        [Permission(Permissions.Order.EditItem)]
        public async Task<IActionResult> EditOrderItem(AutoUpdateOrderItemModel model)
        {
            var context = new UpdateOrderDetailsContext
            {
                UpdateOrderItem = true,
                AdjustInventory = model.AdjustInventory,
                UpdateRewardPoints = model.UpdateRewardPoints,
                UpdateTotals = model.UpdateTotals,
                NewQuantity = model.NewQuantity ?? 0,
                NewUnitPriceInclTax = model.NewUnitPriceInclTax,
                NewUnitPriceExclTax = model.NewUnitPriceExclTax,
                NewTaxRate = model.NewTaxRate,
                NewDiscountInclTax = model.NewDiscountInclTax,
                NewDiscountExclTax = model.NewDiscountExclTax,
                NewPriceInclTax = model.NewPriceInclTax,
                NewPriceExclTax = model.NewPriceExclTax
            };

            // INFO: UpdateOrderDetailsAsync performs commit.
            var orderItem = await _orderProcessingService.UpdateOrderDetailsAsync(model.Id, context);
            if (orderItem != null)
            {
                return NotFound();
            }

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), orderItem.Order.GetOrderNumber());
            TempData[UpdateOrderDetailsContext.InfoKey] = context.ToString(Services.Localization);

            return RedirectToAction(nameof(Edit), new { id = orderItem.OrderId });
        }

        [HttpPost]
        [Permission(Permissions.Order.EditItem)]
        public async Task<IActionResult> DeleteOrderItem(AutoUpdateOrderItemModel model)
        {
            var context = new UpdateOrderDetailsContext
            {
                NewQuantity = 0,
                AdjustInventory = model.AdjustInventory,
                UpdateRewardPoints = model.UpdateRewardPoints,
                UpdateTotals = model.UpdateTotals
            };

            var orderItem = await _orderProcessingService.UpdateOrderDetailsAsync(model.Id, context);
            if (orderItem == null)
            {
                return NotFound();
            }

            var orderId = orderItem.OrderId;
            var orderNumber = orderItem.Order.GetOrderNumber();

            _db.OrderItems.Remove(orderItem);
            await _db.SaveChangesAsync();

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), orderNumber);
            TempData[UpdateOrderDetailsContext.InfoKey] = context.ToString(Services.Localization);

            return RedirectToAction(nameof(Edit), new { id = orderId });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired(FormValueRequirementOperator.StartsWith, "btnAddReturnRequest")]
        [Permission(Permissions.Order.ReturnRequest.Create)]
        public async Task<IActionResult> AddReturnRequest(int id, IFormCollection form)
        {
            var order = await _db.Orders
                .Include(x => x.OrderItems)
                .Include(x => x.Customer)
                .ThenInclude(x => x.ReturnRequests)
                .FindByIdAsync(id);
            if (order == null)
            {
                return NotFound();
            }

            var orderItem = GetOrderItemByFormValue(order, "btnAddReturnRequest", form);
            if (orderItem == null)
            {
                return NotFound();
            }

            if (orderItem.Quantity > 0)
            {
                var returnRequest = new ReturnRequest
                {
                    StoreId = order.StoreId,
                    OrderItemId = orderItem.Id,
                    Quantity = orderItem.Quantity,
                    CustomerId = order.CustomerId,
                    ReasonForReturn = string.Empty,
                    RequestedAction = string.Empty,
                    StaffNotes = string.Empty,
                    ReturnRequestStatus = ReturnRequestStatus.Pending
                };

                order.Customer.ReturnRequests.Add(returnRequest);
                await _db.SaveChangesAsync();

                return RedirectToAction("Edit", "ReturnRequest", new { id = returnRequest.Id });
            }

            return RedirectToAction(nameof(Edit), new { id = order.Id });
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired(FormValueRequirementOperator.StartsWith, "btnResetDownloadCount")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> ResetDownloadCount(int id, IFormCollection form)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            var orderItem = GetOrderItemByFormValue(order, "btnResetDownloadCount", form);
            if (orderItem == null)
            {
                return NotFound();
            }

            orderItem.DownloadCount = 0;
            await _db.SaveChangesAsync();

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

            var model = new OrderModel();
            await PrepareOrderModel(model, order);

            return View(model);
        }

        [HttpPost, ActionName("Edit")]
        [FormValueRequired(FormValueRequirementOperator.StartsWith, "btnPvActivateDownload")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> ActivateDownloadOrderItem(int id, IFormCollection form)
        {
            var order = await GetOrderWithIncludes(id);
            if (order == null)
            {
                return NotFound();
            }

            var orderItem = GetOrderItemByFormValue(order, "btnPvActivateDownload", form);
            if (orderItem == null)
            {
                return NotFound();
            }

            orderItem.IsDownloadActivated = !orderItem.IsDownloadActivated;
            await _db.SaveChangesAsync();

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

            var model = new OrderModel();
            await PrepareOrderModel(model, order);

            return View(model);
        }

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> UploadLicenseFilePopup(int id, int orderItemId)
        {
            var order = await _db.Orders
                .IncludeOrderItems()
                .FindByIdAsync(id, false);
            if (order == null)
            {
                return NotFound();
            }

            var orderItem = order.OrderItems.FirstOrDefault(x => x.Id == orderItemId);
            if (orderItem == null)
            {
                return NotFound();
            }

            if (!orderItem.Product.IsDownload)
            {
                throw new ArgumentException(T("Admin.Orders.Products.NotDownloadable"));
            }

            var model = new OrderModel.UploadLicenseModel
            {
                LicenseDownloadId = orderItem.LicenseDownloadId ?? 0,
                OldLicenseDownloadId = orderItem.LicenseDownloadId ?? 0,
                OrderId = order.Id,
                OrderItemId = orderItem.Id
            };

            return View(model);
        }

        [HttpPost]
        [FormValueRequired("uploadlicense")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> UploadLicenseFilePopup(string btnId, string formId, OrderModel.UploadLicenseModel model)
        {
            var order = await _db.Orders
                .IncludeOrderItems()
                .FindByIdAsync(model.OrderId);
            if (order == null)
            {
                return NotFound();
            }

            var orderItem = order.OrderItems.FirstOrDefault(x => x.Id == model.OrderItemId);
            if (orderItem == null)
            {
                return NotFound();
            }

            var isUrlDownload = Request.Form["is-url-download-" + model.LicenseDownloadId] == "true";
            var setOldFileToTransient = false;

            if (model.LicenseDownloadId != model.OldLicenseDownloadId && model.LicenseDownloadId != 0 && !isUrlDownload)
            {
                // Insert download if a new file was uploaded.
                var mediaFileInfo = await Services.MediaService.GetFileByIdAsync(model.LicenseDownloadId);

                var download = new Download
                {
                    MediaFile = mediaFileInfo.File,
                    EntityId = model.OrderId,
                    EntityName = "LicenseDownloadId",
                    DownloadGuid = Guid.NewGuid(),
                    UseDownloadUrl = false,
                    DownloadUrl = string.Empty,
                    UpdatedOnUtc = DateTime.UtcNow,
                    IsTransient = false
                };

                _db.Downloads.Add(download);
                await _db.SaveChangesAsync();

                orderItem.LicenseDownloadId = download.Id;

                setOldFileToTransient = true;
            }
            else if (isUrlDownload)
            {
                var download = await _db.Downloads.FindByIdAsync(model.LicenseDownloadId);

                download.IsTransient = false;
                download.UpdatedOnUtc = DateTime.UtcNow;
                orderItem.LicenseDownloadId = model.LicenseDownloadId;

                setOldFileToTransient = true;
            }

            if (setOldFileToTransient && model.OldLicenseDownloadId > 0)
            {
                // Set old download to transient if LicenseDownloadId is 0.
                var oldDownload = await _db.Downloads.FindByIdAsync(model.OldLicenseDownloadId);
                oldDownload.IsTransient = true;
                oldDownload.UpdatedOnUtc = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

            ViewBag.RefreshPage = true;
            ViewBag.btnId = btnId;
            ViewBag.formId = formId;

            return View(model);
        }

        [HttpPost, ActionName("UploadLicenseFilePopup")]
        [FormValueRequired("deletelicense")]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> DeleteLicenseFilePopup(string btnId, string formId, OrderModel.UploadLicenseModel model)
        {
            var order = await _db.Orders
                .IncludeOrderItems()
                .FindByIdAsync(model.OrderId);
            if (order == null)
            {
                return NotFound();
            }

            var orderItem = order.OrderItems.FirstOrDefault(x => x.Id == model.OrderItemId);
            if (orderItem == null)
            {
                return NotFound();
            }

            // Set deleted file to transient.
            var download = await _db.Downloads.FindByIdAsync(model.OldLicenseDownloadId);
            download.IsTransient = true;

            // Detach license.
            orderItem.LicenseDownloadId = null;

            await _db.SaveChangesAsync();

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

            ViewBag.RefreshPage = true;
            ViewBag.btnId = btnId;
            ViewBag.formId = formId;

            return View(model);
        }

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> AddressEdit(int addressId, int orderId)
        {
            if (!await _db.Orders.AnyAsync(x => x.Id == orderId))
            {
                return NotFound();
            }

            var address = await _db.Addresses.FindByIdAsync(addressId);
            if (address == null)
            {
                return NotFound();
            }

            var model = new OrderAddressModel(orderId);
            await PrepareOrderAddressModel(model, address);

            return View(model);
        }

        [HttpPost]
        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> AddressEdit(OrderAddressModel model)
        {
            var order = await _db.Orders.FindByIdAsync(model.OrderId);
            if (order == null)
            {
                return NotFound();
            }

            var address = await _db.Addresses.FindByIdAsync(model.Address.Id);
            if (address == null)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                MiniMapper.Map(model.Address, address);
                await _db.SaveChangesAsync();

                await Services.EventPublisher.PublishOrderUpdatedAsync(order);
                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());
                NotifySuccess(T("Admin.Common.DataSuccessfullySaved"));

                return RedirectToAction(nameof(AddressEdit), new { addressId = model.Address.Id, orderId = model.OrderId });
            }

            await PrepareOrderAddressModel(model, address);

            return View(model);
        }

        // INFO: shipment action methods moved to new ShipmentController and were renamed in some cases.
        #endregion

        #region Order notes

        [HttpPost]
        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> OrderNoteList(GridCommand command, int orderId)
        {
            var order = await _db.Orders
                .Include(x => x.OrderNotes)
                .FindByIdAsync(orderId);

            if (order != null)
            {
                return NotFound();
            }

            var rows = order.OrderNotes
                .OrderByDescending(x => x.CreatedOnUtc)
                .Select(x => new OrderModel.OrderNote
                {
                    Id = x.Id,
                    OrderId = x.OrderId,
                    DisplayToCustomer = x.DisplayToCustomer,
                    Note = x.FormatOrderNoteText(),
                    CreatedOn = Services.DateTimeHelper.ConvertToUserTime(x.CreatedOnUtc, DateTimeKind.Utc)
                })
                .ToList();

            if (order.HasNewPaymentNotification)
            {
                order.HasNewPaymentNotification = false;
                await _db.SaveChangesAsync();
            }

            return Json(new GridModel<OrderModel.OrderNote>
            {
                Rows = rows,
                Total = order.OrderNotes.Count
            });
        }

        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> OrderNoteInsert(int orderId, bool displayToCustomer, string message)
        {
            var order = await _db.Orders.FindByIdAsync(orderId);
            if (order == null)
            {
                return NotFound();
            }

            var orderNote = new OrderNote
            {
                DisplayToCustomer = displayToCustomer,
                Note = message,
                CreatedOnUtc = DateTime.UtcNow,
            };

            order.OrderNotes.Add(orderNote);
            await _db.SaveChangesAsync();

            if (displayToCustomer)
            {
                await _messageFactory.SendNewOrderNoteAddedCustomerNotificationAsync(orderNote, Services.WorkContext.WorkingLanguage.Id);
            }

            Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

            return Json(new { Result = true });
        }

        [Permission(Permissions.Order.Update)]
        public async Task<IActionResult> OrderNoteDelete(GridSelection selection, int orderId)
        {
            var success = false;
            var order = await _db.Orders
                .Include(x => x.OrderNotes)
                .FindByIdAsync(orderId);

            if (order != null)
            {
                var ids = selection.GetEntityIds().ToArray();
                var orderNotes = order.OrderNotes.Where(x => ids.Contains(x.Id));

                if (orderNotes.Any())
                {
                    _db.OrderNotes.RemoveRange(orderNotes);
                    await _db.SaveChangesAsync();

                    await Services.EventPublisher.PublishOrderUpdatedAsync(order);
                    Services.ActivityLogger.LogActivity(KnownActivityLogTypes.EditOrder, T("ActivityLog.EditOrder"), order.GetOrderNumber());

                    success = true;
                }
            }

            return Json(new { Success = success });
        }

        #endregion

        // TODO: (mg) (core) really port old way to add product to order (tons of code, did not support product bundles)?. Or do similar to customer impersonate approach?
        #region Add product to order

        [Permission(Permissions.Order.EditItem)]
        public IActionResult AddProductToOrder(int orderId)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Reports

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> BestsellersReport()
        {
            var countries = await _db.Countries
                .AsNoTracking()
                .Where(x => x.AllowsBilling)
                .ToListAsync();

            var countryItems = countries
                .Select(x => new SelectListItem { Text = x.Name, Value = x.Id.ToString() })
                .ToList();
            countryItems.Insert(0, new SelectListItem { Text = T("Admin.Address.SelectCountry"), Value = "0" });

            ViewBag.Countries = countryItems;
            ViewBag.DisplayProductPictures = _adminAreaSettings.DisplayProductPictures;

            return View(new BestsellersReportModel());
        }

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> BestsellersReportList(GridCommand command, BestsellersReportModel model)
        {
            var dtHelper = Services.DateTimeHelper;
            var sorting = ReportSorting.ByAmountDesc;

            DateTime? startDate = model.StartDate == null
                ? null
                : dtHelper.ConvertToUtcTime(model.StartDate.Value, dtHelper.CurrentTimeZone);

            DateTime? endDate = model.EndDate == null
                ? null
                : dtHelper.ConvertToUtcTime(model.EndDate.Value, dtHelper.CurrentTimeZone).AddDays(1);

            if (command.Sorting?.Any() ?? false)
            {
                var sort = command.Sorting.First();
                if (sort.Member == nameof(BestsellersReportLineModel.TotalQuantity))
                {
                    sorting = sort.Descending
                        ? ReportSorting.ByQuantityDesc
                        : ReportSorting.ByQuantityAsc;
                }
                else if (sort.Member == nameof(BestsellersReportLineModel.TotalAmount))
                {
                    sorting = sort.Descending
                        ? ReportSorting.ByAmountDesc
                        : ReportSorting.ByAmountAsc;
                }
            }

            var orderItemQuery =
                from oi in _db.OrderItems
                join o in _db.Orders on oi.OrderId equals o.Id
                join p in _db.Products on oi.ProductId equals p.Id
                where
                    (!startDate.HasValue || startDate.Value <= o.CreatedOnUtc) &&
                    (!endDate.HasValue || endDate.Value >= o.CreatedOnUtc) &&
                    (model.OrderStatusId == 0 || model.OrderStatusId == o.OrderStatusId) &&
                    (model.PaymentStatusId == 0 || model.PaymentStatusId == o.PaymentStatusId) &&
                    (model.ShippingStatusId == 0 || model.ShippingStatusId == o.ShippingStatusId) &&
                    (model.BillingCountryId == 0 || model.BillingCountryId == o.BillingAddress.CountryId) &&
                    !p.IsSystemProduct
                select oi;

            //var watch = Stopwatch.StartNew();

            // TODO: (mg) (core) bestsellers list is slow due to GroupBy.
            // The linq query has not changed but is translated by Core into completely different SQL.
            // It's a bit tricky. Re-working the query can lead to other performance issues but there should be a solution for that.
            var reportLines = await orderItemQuery
                .SelectAsBestsellersReportLine(sorting)
                .ToPagedList(command)
                .LoadAsync();

            //watch.Stop();
            //$"Bestsellers list {watch.ElapsedMilliseconds}ms, {reportLines.TotalCount} products.".Dump();

            //var orderQuery =
            //    from o in _db.Orders
            //    where
            //        (!startDate.HasValue || startDate.Value <= o.CreatedOnUtc) &&
            //        (!endDate.HasValue || endDate.Value >= o.CreatedOnUtc) &&
            //        (model.OrderStatusId == 0 || model.OrderStatusId == o.OrderStatusId) &&
            //        (model.PaymentStatusId == 0 || model.PaymentStatusId == o.PaymentStatusId) &&
            //        (model.ShippingStatusId == 0 || model.ShippingStatusId == o.ShippingStatusId) &&
            //        (model.BillingCountryId == 0 || model.BillingCountryId == o.BillingAddress.CountryId)
            //    select o;                

            //var query =
            //    from p in _db.Products
            //    where !p.IsSystemProduct
            //    select new
            //    {
            //        ProductId = p.Id,
            //        TotalAmount =
            //            (from oi in _db.OrderItems
            //            join o in orderQuery on oi.OrderId equals o.Id
            //            where oi.ProductId == p.Id
            //            select oi.PriceExclTax).Sum(),
            //        TotalQuantity =
            //            (from oi in _db.OrderItems
            //             join o in orderQuery on oi.OrderId equals o.Id
            //             where oi.ProductId == p.Id
            //             select oi.Quantity).Sum()
            //    };

            //var reportLines = await query
            //    .OrderByDescending(x => x.TotalAmount)
            //    .ToPagedList(command)
            //    .LoadAsync();


            var rows = await reportLines.MapAsync(Services, true);

            return Json(new GridModel<BestsellersReportLineModel>
            {
                Rows = rows,
                Total = reportLines.TotalCount
            });
        }

        [Permission(Permissions.Order.Read)]
        public IActionResult NeverSoldReport()
        {
            ViewBag.DisplayProductPictures = _adminAreaSettings.DisplayProductPictures;

            return View(new NeverSoldReportModel());
        }

        [Permission(Permissions.Order.Read)]
        public async Task<IActionResult> NeverSoldReportList(GridCommand command, NeverSoldReportModel model)
        {
            var dtHelper = Services.DateTimeHelper;
            var groupedProductId = (int)ProductType.GroupedProduct;

            DateTime? startDate = model.StartDate == null
                ? null
                : dtHelper.ConvertToUtcTime(model.StartDate.Value, dtHelper.CurrentTimeZone);

            DateTime? endDate = model.EndDate == null
                ? null
                : dtHelper.ConvertToUtcTime(model.EndDate.Value, dtHelper.CurrentTimeZone).AddDays(1);

            var subQuery =
                from oi in _db.OrderItems
                join o in _db.Orders on oi.OrderId equals o.Id
                where
                    (!startDate.HasValue || startDate.Value <= o.CreatedOnUtc) &&
                    (!endDate.HasValue || endDate.Value >= o.CreatedOnUtc)
                select oi.ProductId;

            var productQuery = 
                from p in _db.Products.AsNoTracking()
                where !subQuery.Distinct().Contains(p.Id) && p.ProductTypeId != groupedProductId && !p.IsSystemProduct
                orderby p.Name
                select p;

            var products = await productQuery
                .ApplyGridCommand(command)
                .ToPagedList(command)
                .LoadAsync();

            var rows = await products.MapAsync(Services.MediaService);

            return Json(new GridModel<ProductOverviewModel>
            {
                Rows = rows,
                Total = products.TotalCount
            });
        }

        #endregion

        #region Utilities

        private async Task<Order> GetOrderWithIncludes(int id, bool tracked = true)
        {
            var order = await _db.Orders
                .IncludeCustomer(true)
                .IncludeOrderItems()
                .IncludeShipments()
                .IncludeGiftCardHistory()
                .IncludeBillingAddress()
                .FindByIdAsync(id, tracked);

            return order;
        }

        private async Task PrepareOrderOverviewModel(OrderOverviewModel model, Order order)
        {
            MiniMapper.Map(order, model);

            model.OrderNumber = order.GetOrderNumber();
            model.StoreName = Services.StoreContext.GetStoreById(order.StoreId)?.Name ?? StringExtensions.NotAvailable;
            model.CustomerName = order.BillingAddress.GetFullName().NaIfEmpty();
            model.CustomerEmail = order.BillingAddress?.Email;
            model.OrderTotalString = Format(order.OrderTotal);
            model.OrderStatusString = await Services.Localization.GetLocalizedEnumAsync(order.OrderStatus);
            model.PaymentStatusString = await Services.Localization.GetLocalizedEnumAsync(order.PaymentStatus);
            model.ShippingStatusString = await Services.Localization.GetLocalizedEnumAsync(order.ShippingStatus);
            model.ShippingMethod = order.ShippingMethod.NaIfEmpty();
            model.CreatedOn = Services.DateTimeHelper.ConvertToUserTime(order.CreatedOnUtc, DateTimeKind.Utc);
            model.UpdatedOn = Services.DateTimeHelper.ConvertToUserTime(order.UpdatedOnUtc, DateTimeKind.Utc);
            model.EditUrl = Url.Action("Edit", "Order", new { id = order.Id });
            model.CustomerEditUrl = Url.Action("Edit", "Customer", new { id = order.CustomerId });
        }

        private async Task PrepareOrderModel(OrderModel model, Order order)
        {
            Guard.NotNull(model, nameof(model));
            Guard.NotNull(order, nameof(order));

            var language = Services.WorkContext.WorkingLanguage;
            var store = Services.StoreContext.GetStoreById(order.StoreId);
            var taxRates = order.TaxRatesDictionary;

            MiniMapper.Map(order, model);
            await PrepareOrderOverviewModel(model, order);

            if (order.AffiliateId != 0)
            {
                var affiliate = await _db.Affiliates
                    .Include(x => x.Address)
                    .FindByIdAsync(order.AffiliateId);

                model.AffiliateFullName = affiliate?.Address?.GetFullName() ?? StringExtensions.NotAvailable;
            }

            model.DisplayPdfInvoice = _pdfSettings.Enabled;
            model.OrderSubtotalInclTaxString = Format(order.OrderSubtotalInclTax, true);
            model.OrderSubtotalExclTaxString = Format(order.OrderSubtotalExclTax, false);

            if (order.OrderSubTotalDiscountInclTax > decimal.Zero)
            {
                model.OrderSubTotalDiscountInclTaxString = Format(order.OrderSubTotalDiscountInclTax, true);
            }
            if (order.OrderSubTotalDiscountExclTax > decimal.Zero)
            {
                model.OrderSubTotalDiscountExclTaxString = Format(order.OrderSubTotalDiscountExclTax, false);
            }

            model.OrderShippingInclTaxString = Format(order.OrderShippingInclTax, true, null, PricingTarget.ShippingCharge);
            model.OrderShippingExclTaxString = Format(order.OrderShippingExclTax, false, null, PricingTarget.ShippingCharge);

            if (order.PaymentMethodAdditionalFeeInclTax != decimal.Zero)
            {
                model.PaymentMethodAdditionalFeeInclTaxString = Format(order.PaymentMethodAdditionalFeeInclTax, true, null, PricingTarget.PaymentFee);
                model.PaymentMethodAdditionalFeeExclTaxString = Format(order.PaymentMethodAdditionalFeeExclTax, false, null, PricingTarget.PaymentFee);
            }

            model.DisplayTaxRates = _taxSettings.DisplayTaxRates && taxRates.Any();
            model.DisplayTax = !model.DisplayTaxRates;
            model.OrderTaxString = Format(order.OrderTax);

            model.TaxRatesList = taxRates
                .Select(x => new OrderModel.TaxRate
                {
                    Rate = _taxService.FormatTaxRate(x.Key),
                    Value = Format(x.Value)
                })
                .ToList();

            if (order.OrderDiscount > 0)
            {
                model.OrderDiscountString = Format(-order.OrderDiscount);
            }

            if (order.OrderTotalRounding != decimal.Zero)
            {
                model.OrderTotalRoundingString = Format(order.OrderTotalRounding);
            }

            model.GiftCards = order.GiftCardUsageHistory
                .Select(x => new OrderModel.GiftCard
                {
                    CouponCode = x.GiftCard.GiftCardCouponCode,
                    Amount = Format(-x.UsedValue)
                })
                .ToList();

            if (order.RedeemedRewardPointsEntry != null)
            {
                model.RedeemedRewardPoints = -order.RedeemedRewardPointsEntry.Points;
                model.RedeemedRewardPointsAmountString = Format(-order.RedeemedRewardPointsEntry.UsedAmount);
            }

            if (order.CreditBalance > decimal.Zero)
            {
                model.CreditBalanceString = Format(-order.CreditBalance);
            }

            if (order.RefundedAmount > decimal.Zero)
            {
                model.RefundedAmountString = Format(order.RefundedAmount);
            }

            if (order.AllowStoringCreditCardNumber)
            {
                model.AllowStoringCreditCardNumber = true;
                model.CardType = _encryptor.DecryptText(order.CardType);
                model.CardName = _encryptor.DecryptText(order.CardName);
                model.CardNumber = _encryptor.DecryptText(order.CardNumber);
                model.CardCvv2 = _encryptor.DecryptText(order.CardCvv2);

                var cardExpirationMonthDecrypted = _encryptor.DecryptText(order.CardExpirationMonth);
                if (cardExpirationMonthDecrypted.HasValue() && cardExpirationMonthDecrypted != "0")
                {
                    model.CardExpirationMonth = cardExpirationMonthDecrypted;
                }
                var cardExpirationYearDecrypted = _encryptor.DecryptText(order.CardExpirationYear);
                if (cardExpirationYearDecrypted.HasValue() && cardExpirationYearDecrypted != "0")
                {
                    model.CardExpirationYear = cardExpirationYearDecrypted;
                }
            }
            else
            {
                var maskedCreditCardNumberDecrypted = _encryptor.DecryptText(order.MaskedCreditCardNumber);
                if (maskedCreditCardNumberDecrypted.HasValue())
                {
                    model.CardNumber = maskedCreditCardNumberDecrypted;
                }
            }

            if (order.AllowStoringDirectDebit)
            {
                model.AllowStoringDirectDebit = true;
                model.DirectDebitAccountHolder = _encryptor.DecryptText(order.DirectDebitAccountHolder);
                model.DirectDebitAccountNumber = _encryptor.DecryptText(order.DirectDebitAccountNumber);
                model.DirectDebitBankCode = _encryptor.DecryptText(order.DirectDebitBankCode);
                model.DirectDebitBankName = _encryptor.DecryptText(order.DirectDebitBankName);
                model.DirectDebitBIC = _encryptor.DecryptText(order.DirectDebitBIC);
                model.DirectDebitCountry = _encryptor.DecryptText(order.DirectDebitCountry);
                model.DirectDebitIban = _encryptor.DecryptText(order.DirectDebitIban);
            }

            var pm = await _paymentService.LoadPaymentMethodBySystemNameAsync(order.PaymentMethodSystemName);
            if (pm != null)
            {
                model.DisplayCompletePaymentNote = order.PaymentStatus == PaymentStatus.Pending && await pm.Value.CanRePostProcessPaymentAsync(order);
                model.PaymentMethod = _moduleManager.GetLocalizedFriendlyName(pm.Metadata);
            }
            if (model.PaymentMethod.IsEmpty())
            {
                model.PaymentMethod = order.PaymentMethodSystemName;
            }

            model.CanCancelOrder = order.CanCancelOrder();
            model.CanCompleteOrder = order.CanCompleteOrder();
            model.CanCapture = await _orderProcessingService.CanCaptureAsync(order);
            model.CanMarkOrderAsPaid = order.CanMarkOrderAsPaid();
            model.CanRefund = await _orderProcessingService.CanRefundAsync(order);
            model.CanRefundOffline = order.CanRefundOffline();
            model.CanPartiallyRefund = await _orderProcessingService.CanPartiallyRefundAsync(order, decimal.Zero);
            model.CanPartiallyRefundOffline = order.CanPartiallyRefundOffline(decimal.Zero);
            model.CanVoid = await _orderProcessingService.CanVoidAsync(order);
            model.CanVoidOffline = order.CanVoidOffline();

            model.MaxAmountToRefund = order.OrderTotal - order.RefundedAmount;
            model.MaxAmountToRefundString = Format(model.MaxAmountToRefund);

            model.RecurringPaymentId = await _db.RecurringPayments
                .ApplyStandardFilter(order.Id, null, null, true)
                .Select(x => x.Id)
                .FirstOrDefaultAsync();

            model.BillingAddress = MiniMapper.Map<Address, AddressModel>(order.BillingAddress);
            PrepareSettings(model.BillingAddress);

            if (order.ShippingStatus != ShippingStatus.ShippingNotRequired)
            {
                var shipTo = order.ShippingAddress;
                var googleAddressQuery = $"{shipTo.Address1} {shipTo.ZipPostalCode} {shipTo.City} {shipTo.Country?.Name ?? string.Empty}";

                model.ShippingAddress = MiniMapper.Map<Address, AddressModel>(shipTo);
                PrepareSettings(model.ShippingAddress);

                model.CanAddNewShipments = order.CanAddItemsToShipment();

                model.ShippingAddressGoogleMapsUrl = Services.ApplicationContext.AppConfiguration.Google.MapsUrl.FormatInvariant(
                    language.UniqueSeoCode.EmptyNull().ToLower(),
                    googleAddressQuery.UrlEncode());
            }
            else
            {
                model.ShippingAddress = new();
            }

            // Purchase order number (we have to find a better to inject this information because it's related to a certain plugin).
            // TODO: (mg) (core) verify plugin systemname Smartstore.PurchaseOrderNumber.
            model.DisplayPurchaseOrderNumber = order.PaymentMethodSystemName.EqualsNoCase("Smartstore.PurchaseOrderNumber");
            model.CheckoutAttributeInfo = HtmlUtility.ConvertPlainTextToTable(HtmlUtility.ConvertHtmlToPlainText(order.CheckoutAttributeDescription));
            model.HasDownloadableProducts = order.OrderItems.Any(x => x.Product.IsDownload);
            model.AutoUpdateOrderItemInfo = TempData[UpdateOrderDetailsContext.InfoKey] as string;

            model.AutoUpdateOrderItem = new AutoUpdateOrderItemModel
            {
                Caption = T("Admin.Orders.EditOrderDetails"),
                ShowUpdateTotals = order.OrderStatusId <= (int)OrderStatus.Pending,
                UpdateTotals = order.OrderStatusId <= (int)OrderStatus.Pending,
                // UpdateRewardPoints only visible for unpending orders (see RewardPointsSettingsValidator).
                ShowUpdateRewardPoints = order.OrderStatusId > (int)OrderStatus.Pending && order.RewardPointsWereAdded,
                UpdateRewardPoints = order.RewardPointsWereAdded
            };

            model.Items = await CreateOrderItemsModels(order);
        }

        private async Task<List<OrderModel.OrderItemModel>> CreateOrderItemsModels(Order order)
        {
            var result = new List<OrderModel.OrderItemModel>();
            var returnRequestsMap = new Multimap<int, ReturnRequest>();
            var giftCardIdsMap = new Multimap<int, int>();
            var orderItemIds = order.OrderItems.Select(x => x.Id).ToArray();

            if (orderItemIds.Any())
            {
                var returnRequests = await _db.ReturnRequests
                    .AsNoTracking()
                    .ApplyStandardFilter(orderItemIds)
                    .ToListAsync();

                var giftCards = await _db.GiftCards
                    .AsNoTracking()
                    .Where(x => x.PurchasedWithOrderItemId != null && orderItemIds.Contains(x.PurchasedWithOrderItemId.Value))
                    .OrderBy(x => x.Id)
                    .Select(x => new
                    {
                        x.Id,
                        OrderItemId = x.PurchasedWithOrderItemId.Value
                    })
                    .ToListAsync();

                returnRequestsMap = returnRequests.ToMultimap(x => x.OrderItemId, x => x);
                giftCardIdsMap = giftCards.ToMultimap(x => x.OrderItemId, x => x.Id);
            }

            foreach (var item in order.OrderItems)
            {
                var product = item.Product;
                await _productAttributeMaterializer.MergeWithCombinationAsync(product, item.AttributeSelection);

                var model = MiniMapper.Map<OrderItem, OrderModel.OrderItemModel>(item);
                model.ProductName = product.GetLocalized(x => x.Name);
                model.Sku = product.Sku;
                model.ProductType = product.ProductType;
                model.ProductTypeName = product.GetProductTypeLabel(Services.Localization);
                model.ProductTypeLabelHint = product.ProductTypeLabelHint;
                model.IsDownload = product.IsDownload;
                model.DownloadActivationType = product.DownloadActivationType;
                model.UnitPriceInclTaxString = Format(item.UnitPriceInclTax, true, true);
                model.UnitPriceExclTaxString = Format(item.UnitPriceExclTax, false, true);
                model.PriceInclTaxString = Format(item.PriceInclTax, true, true);
                model.PriceExclTaxString = Format(item.PriceExclTax, false, true);
                model.DiscountInclTaxString = Format(item.DiscountAmountInclTax, true, true);
                model.DiscountExclTaxString = Format(item.DiscountAmountExclTax, false, true);

                if (product.IsRecurring)
                {
                    var period = await Services.Localization.GetLocalizedEnumAsync(product.RecurringCyclePeriod);
                    model.RecurringInfo = T("Admin.Orders.Products.RecurringPeriod", product.RecurringCycleLength, period);
                }

                if (returnRequestsMap.ContainsKey(item.Id))
                {
                    model.ReturnRequests = await returnRequestsMap[item.Id]
                        .SelectAsync(async x => new OrderModel.ReturnRequestModel
                        {
                            Id = x.Id,
                            Quantity = x.Quantity,
                            Status = x.ReturnRequestStatus,
                            StatusString = await Services.Localization.GetLocalizedEnumAsync(x.ReturnRequestStatus)
                        })
                        .AsyncToList();
                }

                if (giftCardIdsMap.ContainsKey(item.Id))
                {
                    model.PurchasedGiftCardIds = giftCardIdsMap[item.Id].ToList();
                }

                if (product.ProductType == ProductType.BundledProduct && item.BundleData.HasValue())
                {
                    var bundleData = item.GetBundleData();

                    model.BundlePerItemPricing = product.BundlePerItemPricing;
                    model.BundlePerItemShoppingCart = bundleData.Any(x => x.PerItemShoppingCart);
                    model.BundleItems = bundleData
                        .Select(x => new OrderModel.BundleItemModel
                        {
                            ProductId = x.ProductId,
                            Sku = x.Sku,
                            ProductName = x.ProductName,
                            ProductSeName = x.ProductSeName,
                            VisibleIndividually = x.VisibleIndividually,
                            Quantity = x.Quantity,
                            DisplayOrder = x.DisplayOrder,
                            AttributeInfo = x.AttributesInfo,
                            PriceWithDiscount = model.BundlePerItemShoppingCart
                                ? Format(x.PriceWithDiscount)
                                : null
                        })
                        .ToList();
                }

                result.Add(model);
            }

            return result;
        }

        private async Task PrepareOrderAddressModel(OrderAddressModel model, Address address)
        {
            model.Address = MiniMapper.Map<Address, AddressModel>(address);
            PrepareSettings(model.Address);

            var countries = await _db.Countries
                .AsNoTracking()
                .ApplyStandardFilter(true)
                .ToListAsync();

            model.Address.AvailableCountries = countries
                .Select(x => new SelectListItem { Text = x.Name, Value = x.Id.ToString(), Selected = x.Id == address.CountryId })
                .ToList();

            if (address.CountryId.HasValue)
            {
                var stateProvinces = await _db.StateProvinces
                    .AsNoTracking()
                    .ApplyCountryFilter(address.CountryId.Value)
                    .ToListAsync();

                model.Address.AvailableStates = stateProvinces
                    .Select(x => new SelectListItem { Text = x.Name, Value = x.Id.ToString(), Selected = x.Id == address.StateProvinceId })
                    .ToList();
            }
            else
            {
                model.Address.AvailableStates = new List<SelectListItem>
                {
                    new SelectListItem { Text = T("Admin.Address.OtherNonUS"), Value = "0" }
                };
            }
        }

        private void PrepareSettings(AddressModel model)
        {
            model.ValidateEmailAddress = _addressSettings.ValidateEmailAddress;
            model.CompanyEnabled = _addressSettings.CompanyEnabled;
            model.CompanyRequired = _addressSettings.CompanyRequired;
            model.CountryEnabled = _addressSettings.CountryEnabled;
            model.StateProvinceEnabled = _addressSettings.StateProvinceEnabled;
            model.CityEnabled = _addressSettings.CityEnabled;
            model.CityRequired = _addressSettings.CityRequired;
            model.StreetAddressEnabled = _addressSettings.StreetAddressEnabled;
            model.StreetAddressRequired = _addressSettings.StreetAddressRequired;
            model.StreetAddress2Enabled = _addressSettings.StreetAddress2Enabled;
            model.StreetAddress2Required = _addressSettings.StreetAddress2Required;
            model.ZipPostalCodeEnabled = _addressSettings.ZipPostalCodeEnabled;
            model.ZipPostalCodeRequired = _addressSettings.ZipPostalCodeRequired;
            model.PhoneEnabled = _addressSettings.PhoneEnabled;
            model.PhoneRequired = _addressSettings.PhoneRequired;
            model.FaxEnabled = _addressSettings.FaxEnabled;
            model.FaxRequired = _addressSettings.FaxRequired;
        }

        private OrderItem GetOrderItemByFormValue(Order order, string prefix, IFormCollection form)
        {
            var prefixLength = prefix.Length;

            foreach (var value in form.Keys)
            {
                if (value.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
                {
                    var orderItemId = value[prefixLength..].ToInt();
                    
                    return order.OrderItems.FirstOrDefault(x => x.Id == orderItemId);
                }
            }

            return null;
        }

        private string Format(decimal value, bool priceIncludesTax, bool? displayTaxSuffix = null, PricingTarget target = PricingTarget.Product)
        {
            var format = _currencyService.GetTaxFormat(displayTaxSuffix, priceIncludesTax, target, Services.WorkContext.WorkingLanguage);

            return new Money(value, _primaryCurrency, false, format).ToString(true);
        }

        private string Format(decimal value)
        {
            return new Money(value, _primaryCurrency, false).ToString(true);
        }

        #endregion
    }
}
