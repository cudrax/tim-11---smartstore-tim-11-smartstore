﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Smartstore.ComponentModel;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Common.Settings;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Localization.Routing;
using Smartstore.Core.Logging;
using Smartstore.Core.Messaging;
using Smartstore.Core.Security;
using Smartstore.Core.Stores;
using Smartstore.Core.Web;
using Smartstore.Web.Models.Identity;

namespace Smartstore.Web.Controllers
{
    public class IdentityController : PublicController
    {
        private readonly SmartDbContext _db;
        private readonly UserManager<Customer> _userManager;
        private readonly SignInManager<Customer> _signInManager;
        private readonly RoleManager<CustomerRole> _roleManager;
        private readonly IUserStore<Customer> _userStore;
        private readonly ITaxService _taxService;
        private readonly IAddressService _addressService;
        private readonly IShoppingCartService _shoppingCartService;
        private readonly IMessageFactory _messageFactory;
        private readonly IWebHelper _webHelper;
        private readonly IDateTimeHelper _dateTimeHelper;
        private readonly CustomerSettings _customerSettings;
        private readonly CaptchaSettings _captchaSettings;
        private readonly DateTimeSettings _dateTimeSettings;
        private readonly TaxSettings _taxSettings;
        private readonly LocalizationSettings _localizationSettings;
        private readonly ExternalAuthenticationSettings _externalAuthenticationSettings;
        private readonly RewardPointsSettings _rewardPointsSettings;

        public IdentityController(
            SmartDbContext db,
            UserManager<Customer> userManager,
            SignInManager<Customer> signInManager,
            RoleManager<CustomerRole> roleManager,
            IUserStore<Customer> userStore,
            ITaxService taxService,
            IAddressService addressService,
            IShoppingCartService shoppingCartService,
            IMessageFactory messageFactory,
            IWebHelper webHelper,
            IDateTimeHelper dateTimeHelper,
            CustomerSettings customerSettings,
            CaptchaSettings captchaSettings,
            DateTimeSettings dateTimeSettings,
            TaxSettings taxSettings,
            LocalizationSettings localizationSettings,
            ExternalAuthenticationSettings externalAuthenticationSettings,
            RewardPointsSettings rewardPointsSettings)
        {
            _db = db;
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _userStore = userStore;
            _taxService = taxService;
            _addressService = addressService;
            _shoppingCartService = shoppingCartService;
            _messageFactory = messageFactory;
            _webHelper = webHelper;
            _dateTimeHelper = dateTimeHelper;
            _customerSettings = customerSettings;
            _captchaSettings = captchaSettings;
            _dateTimeSettings = dateTimeSettings;
            _taxSettings = taxSettings;
            _localizationSettings = localizationSettings;
            _externalAuthenticationSettings = externalAuthenticationSettings;
            _rewardPointsSettings = rewardPointsSettings;
        }

        #region Login / Logout / Register

        [HttpGet]
        [RequireSsl, AllowAnonymous, NeverAuthorize, CheckStoreClosed(false)]
        [LocalizedRoute("/login", Name = "Login")]
        public async Task<IActionResult> Login(bool? checkoutAsGuest, string returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl ?? Url.Content("~/");

            var model = new LoginModel
            {
                CustomerLoginType = _customerSettings.CustomerLoginType,
                CheckoutAsGuest = checkoutAsGuest.GetValueOrDefault(),
                DisplayCaptcha = _captchaSettings.CanDisplayCaptcha && _captchaSettings.ShowOnLoginPage,
                DisplayExternalAuth = (await _signInManager.GetExternalAuthenticationSchemesAsync()).Any()
            };

            return View(model);
        }

        [HttpPost]
        [AllowAnonymous, NeverAuthorize]
        [ValidateAntiForgeryToken, ValidateCaptcha, CheckStoreClosed(false)]
        [LocalizedRoute("/login", Name = "Login")]
        public async Task<IActionResult> Login(LoginModel model, string returnUrl, string captchaError)
        {
            if (_captchaSettings.ShowOnLoginPage && captchaError.HasValue())
            {
                ModelState.AddModelError(string.Empty, captchaError);
            }

            ViewBag.ReturnUrl = returnUrl;

            if (ModelState.IsValid)
            {
                Customer customer;
                
                if (model.CustomerLoginType == CustomerLoginType.Username)
                {
                    customer = await _userManager.FindByNameAsync(model.Username.TrimSafe());
                }
                else if (model.CustomerLoginType == CustomerLoginType.Email)
                {
                    customer = await _userManager.FindByEmailAsync(model.Email.TrimSafe());
                }
                else
                {
                    customer = await _userManager.FindByEmailAsync(model.UsernameOrEmail.TrimSafe()) ?? await _userManager.FindByNameAsync(model.UsernameOrEmail.TrimSafe());
                }

                var result = await _signInManager.PasswordSignInAsync(customer, model.Password, model.RememberMe, lockoutOnFailure: false);
                
                if (result.Succeeded)
                {
                    await _shoppingCartService.MigrateCartAsync(Services.WorkContext.CurrentCustomer, customer);

                    Services.ActivityLogger.LogActivity(KnownActivityLogTypes.PublicStoreLogin, T("ActivityLog.PublicStore.Login"), customer);

                    await Services.EventPublisher.PublishAsync(new CustomerSignedInEvent { Customer = customer });

                    if (returnUrl.IsEmpty() 
                        || returnUrl.Contains("/login?", StringComparison.OrdinalIgnoreCase) 
                        || returnUrl.Contains("/passwordrecoveryconfirm", StringComparison.OrdinalIgnoreCase) 
                        || returnUrl.Contains("/activation", StringComparison.OrdinalIgnoreCase) 
                        || !Url.IsLocalUrl(returnUrl))
                    {
                        return RedirectToRoute("Homepage");
                    }

                    return RedirectToReferrer(returnUrl);
                }
                else
                {
                    if (_customerSettings.UserRegistrationType == UserRegistrationType.EmailValidation && customer.Active == false)
                    {
                        ModelState.AddModelError(string.Empty, T("Account.Login.CheckEmailAccount"));
                    }
                    else
                    {
                        ModelState.AddModelError(string.Empty, T("Account.Login.WrongCredentials"));
                    }
                }
            }

            // If we got this far something failed. Redisplay form!
            model.CustomerLoginType = _customerSettings.CustomerLoginType;
            model.DisplayCaptcha = _captchaSettings.CanDisplayCaptcha && _captchaSettings.ShowOnLoginPage;
            model.DisplayExternalAuth = (await _signInManager.GetExternalAuthenticationSchemesAsync()).Any();

            return View(model);
        }

        [NeverAuthorize, CheckStoreClosed(false)]
        [LocalizedRoute("/logout", Name = "Logout")]
        public async Task<IActionResult> Logout()
        {
            var workContext = Services.WorkContext;
            var db = Services.DbContext;

            if (workContext.CurrentImpersonator != null)
            {
                // Logout impersonated customer.
                workContext.CurrentImpersonator.GenericAttributes.ImpersonatedCustomerId = null;
                await db.SaveChangesAsync();

                // Redirect back to customer details page (admin area).
                return RedirectToAction("Edit", "Customer", new { id = workContext.CurrentCustomer.Id, area = "Admin" });
            }
            else
            {
                // Standard logout
                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.PublicStoreLogout, T("ActivityLog.PublicStore.Logout"));

                await _signInManager.SignOutAsync();
                await db.SaveChangesAsync();

                return RedirectToRoute("Homepage");
            }
        }

        [HttpGet]
        [RequireSsl, AllowAnonymous, NeverAuthorize]
        [LocalizedRoute("/register", Name = "Register")]
        public async Task<IActionResult> Register(string returnUrl = null)
        {
            // Check whether registration is allowed.
            if (_customerSettings.UserRegistrationType == UserRegistrationType.Disabled)
            {
                return RedirectToRoute("RegisterResult", new { resultId = (int)UserRegistrationType.Disabled });
            }

            ViewBag.ReturnUrl = returnUrl;

            var model = new RegisterModel();
            await PrepareRegisterModelAsync(model);

            return View(model);
        }

        [HttpPost]
        [AllowAnonymous, NeverAuthorize]
        [ValidateAntiForgeryToken, ValidateCaptcha, ValidateHoneypot]
        [LocalizedRoute("/register", Name = "Register")]
        public async Task<IActionResult> Register(RegisterModel model, string captchaError, string returnUrl = null)
        {
            // Check whether registration is allowed.
            if (_customerSettings.UserRegistrationType == UserRegistrationType.Disabled)
            {
                return RedirectToRoute("RegisterResult", new { resultId = (int)UserRegistrationType.Disabled });
            }

            var customer = Services.WorkContext.CurrentCustomer;
            if (customer.IsRegistered())
            {
                // Already registered customer. 
                await _signInManager.SignOutAsync();

                Services.WorkContext.CurrentCustomer = null;
            }

            if (_captchaSettings.ShowOnRegistrationPage && captchaError.HasValue())
            {
                ModelState.AddModelError(string.Empty, captchaError);
            }

            ViewData["ReturnUrl"] = returnUrl;

            if (ModelState.IsValid)
            {
                customer.Username = model.UserName != null ? model.UserName.Trim() : model.Email.Trim();
                customer.Email = model.Email.Trim();
                customer.PasswordFormat = _customerSettings.DefaultPasswordFormat;
                customer.Active = _customerSettings.UserRegistrationType == UserRegistrationType.Standard;
                customer.CreatedOnUtc = DateTime.UtcNow;
                customer.LastActivityDateUtc = DateTime.UtcNow;

                var result = await _userManager.UpdateAsync(customer);

                if (result.Succeeded)
                {
                    var addPasswordResult = await _userManager.AddPasswordAsync(customer, model.Password);

                    if (addPasswordResult.Succeeded)
                    {
                        // Update customer properties.
                        await MapRegisterModelToCustomerAsync(customer, model);

                        return await FinalizeCustomerRegistrationAsync(customer, returnUrl);
                    }
                    else
                    {
                        AddErrors(addPasswordResult);
                    }   
                }

                AddErrors(result);
            }

            // If we got this far something failed. Redisplay form.
            await PrepareRegisterModelAsync(model);
            return View(model);
        }

        [HttpGet]
        [RequireSsl, AllowAnonymous, NeverAuthorize]
        [LocalizedRoute("/registerresult/{resultId:int}", Name = "RegisterResult")]
        public IActionResult RegisterResult(int resultId)
        {
            var resultText = string.Empty;
            switch ((UserRegistrationType)resultId)
            {
                case UserRegistrationType.Disabled:
                    resultText = T("Account.Register.Result.Disabled");
                    break;
                case UserRegistrationType.Standard:
                    resultText = T("Account.Register.Result.Standard");
                    break;
                case UserRegistrationType.AdminApproval:
                    resultText = T("Account.Register.Result.AdminApproval");
                    break;
                case UserRegistrationType.EmailValidation:
                    resultText = T("Account.Register.Result.EmailValidation");
                    break;
                default:
                    break;
            }

            ViewBag.RegisterResult = resultText;
            return View();
        }

        [HttpGet]
        [RequireSsl, AllowAnonymous, NeverAuthorize]
        [LocalizedRoute("/customer/activation", Name = "AccountActivation")]
        public async Task<IActionResult> AccountActivation(string token, string email)
        {
            var customer = await _userManager.FindByEmailAsync(email);
                
            if (customer == null)
            {
                NotifyError(T("Account.AccountActivation.InvalidEmailOrToken"));
                return RedirectToRoute("Homepage");
            }

            // Validate token & set user to active.
            var confirmed = await _userManager.ConfirmEmailAsync(customer, token);

            if (!confirmed.Succeeded)
            {
                NotifyError(T("Account.AccountActivation.InvalidEmailOrToken"));
                return RedirectToRoute("HomePage");
            }

            // If token wasn't proved invalid by ConfirmEmailAsync() a few lines above, Customer.Active was set to true & AccountActivationToken was resetted in UserStore.
            // So we better save here.
            await _db.SaveChangesAsync();

            // Send welcome message.
            await _messageFactory.SendCustomerWelcomeMessageAsync(customer, Services.WorkContext.WorkingLanguage.Id);

            ViewBag.ActivationResult = T("Account.AccountActivation.Activated");

            return View();
        }

        #endregion

        #region Change password

        [RequireSsl]
        public IActionResult ChangePassword()
        {
            if (!Services.WorkContext.CurrentCustomer.IsRegistered())
                return new UnauthorizedResult();

            return View(new ChangePasswordModel());
        }

        [HttpPost]
        public async Task<IActionResult> ChangePassword(ChangePasswordModel model)
        {
            var customer = Services.WorkContext.CurrentCustomer;

            if (!customer.IsRegistered())
                return new UnauthorizedResult();

            if (ModelState.IsValid)
            {
                var changePasswordResult = await _userManager.ChangePasswordAsync(customer, model.OldPassword, model.NewPassword);
                
                if (changePasswordResult.Succeeded)
                {
                    model.Result = T("Account.ChangePassword.Success");
                    return View(model);
                }
                else
                {
                    foreach (var error in changePasswordResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                }
            }

            return View(model);
        }

        #endregion

        #region Password recovery

        [RequireSsl]
        [LocalizedRoute("/passwordrecovery", Name = "PasswordRecovery")]
        public IActionResult PasswordRecovery()
        {
            return View(new PasswordRecoveryModel());
        }

        [HttpPost]
        [LocalizedRoute("/passwordrecovery", Name = "PasswordRecovery")]
        [FormValueRequired("send-email")]
        public async Task<IActionResult> PasswordRecovery(PasswordRecoveryModel model)
        {
            if (ModelState.IsValid)
            {
                var customer = await _userManager.FindByEmailAsync(model.Email);

                if (customer != null && customer.Active)
                {
                    var token = await _userManager.GeneratePasswordResetTokenAsync(customer);
                    customer.GenericAttributes.PasswordRecoveryToken = token;
                    await _db.SaveChangesAsync();

                    await _messageFactory.SendCustomerPasswordRecoveryMessageAsync(customer, Services.WorkContext.WorkingLanguage.Id);

                    model.ResultMessage = T("Account.PasswordRecovery.EmailHasBeenSent");
                    model.ResultState = PasswordRecoveryResultState.Success;
                }
                else
                {
                    model.ResultMessage = T("Account.PasswordRecovery.EmailNotFound");
                    model.ResultState = PasswordRecoveryResultState.Error;
                }

                return View(model);
            }

            // If we got this far something failed. Redisplay form.
            return View(model);
        }

        [RequireSsl]
        public IActionResult PasswordRecoveryConfirm(string token, string email)
        {
            var model = new PasswordRecoveryConfirmModel
            {
                Email = email,
                Token = token
            };

            return View(model);
        }

        [HttpPost, ActionName("PasswordRecoveryConfirm")]
        [FormValueRequired("set-password")]
        public async Task<IActionResult> PasswordRecoveryConfirmPOST(PasswordRecoveryConfirmModel model)
        {
            var customer = await _userManager.FindByEmailAsync(model.Email);

            if (ModelState.IsValid)
            {
                var response = await _userManager.ResetPasswordAsync(customer, model.Token, model.NewPassword);
                
                if (response.Succeeded)
                {
                    customer.GenericAttributes.PasswordRecoveryToken = string.Empty;
                    await _db.SaveChangesAsync();
                    model.SuccessfullyChanged = true;
                    model.Result = T("Account.PasswordRecovery.PasswordHasBeenChanged");
                }
                else
                {
                    NotifyError(T("Account.PasswordRecoveryConfirm.InvalidEmailOrToken"));
                    return RedirectToAction("PasswordRecoveryConfirm", new { token = model.Token, email = model.Email });
                }

                return View(model);
            }

            // If we got this far something failed. Redisplay form.
            return View(model);
        }

        #endregion

        #region External login

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ExternalLogin(string provider, string returnUrl = null)
        {
            var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Identity");
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
            properties.AllowRefresh = true;
            properties.IsPersistent = true;

            return Challenge(properties, provider);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ExternalLoginCallback(string returnUrl = null, string remoteError = null)
        {
            if (remoteError != null)
            {
                NotifyError(remoteError);
                return RedirectToAction(nameof(Login));
            }
            
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return RedirectToAction(nameof(Login));
            }

            // Sign in the user with this external login provider if the user already has a login.
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, false);
            if (result.Succeeded)
            {
                Services.ActivityLogger.LogActivity(KnownActivityLogTypes.PublicStoreLogin, T("ActivityLog.PublicStore.LoginExternal"), info.LoginProvider);
                return RedirectToRoute("Homepage");
            }
            else
            {
                // User doesn't have an account yet.
                if (_customerSettings.UserRegistrationType != UserRegistrationType.Disabled)
                {
                    var customer = new Customer
                    {
                        Username = info.Principal.FindFirstValue(ClaimTypes.Name),
                        Email = info.Principal.FindFirstValue(ClaimTypes.Email),
                        PasswordFormat = _customerSettings.DefaultPasswordFormat,
                        Active = _customerSettings.UserRegistrationType == UserRegistrationType.Standard,
                        CreatedOnUtc = DateTime.UtcNow,
                        LastActivityDateUtc = DateTime.UtcNow
                    };

                    var createResult = await _userManager.CreateAsync(customer);
                    if (createResult.Succeeded)
                    {
                        // INFO: This creates the external auth record
                        createResult = await _userManager.AddLoginAsync(customer, info);
                        if (createResult.Succeeded)
                        {
                            return await FinalizeCustomerRegistrationAsync(customer, returnUrl);
                        }

                        // migrate shopping cart.
                        await _shoppingCartService.MigrateCartAsync(Services.WorkContext.CurrentCustomer, customer);

                        Services.ActivityLogger.LogActivity(KnownActivityLogTypes.PublicStoreLogin, T("ActivityLog.PublicStore.Login"), customer);

                        await Services.EventPublisher.PublishAsync(new CustomerSignedInEvent { Customer = customer });
                    }

                    // Display errors to user.
                    foreach (var error in createResult.Errors)
                    {
                        NotifyError(error.Description);
                    }
                }
                else
                {
                    // Creating new accounts is disabled. Display to user.
                    NotifyError(T("Account.Register.Result.Disabled"));
                }

                return RedirectToLocal(returnUrl);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ExternalErrorCallback(string provider, string errorMessage)
        {
            if (provider.HasValue() || errorMessage.HasValue())
            {
                Logger.Error($"Error from external provider {provider}: { errorMessage }");
            }

            NotifyError(T("ExternalAuthentication.ConfigError"));
            return RedirectToAction(nameof(Login));
        }

        #endregion

        #region Access

        [HttpGet]
        [AllowAnonymous, NeverAuthorize]
        [LocalizedRoute("/access-denied", Name = "AccessDenied")]
        public IActionResult AccessDenied(string returnUrl = null)
        {
            throw new AccessDeniedException(null, returnUrl);
        }

        #endregion

        #region Helpers

        private async Task PrepareRegisterModelAsync(RegisterModel model)
        {
            model.AllowCustomersToSetTimeZone = _dateTimeSettings.AllowCustomersToSetTimeZone;
            model.DisplayVatNumber = _taxSettings.EuVatEnabled;
            model.VatRequired = _taxSettings.VatRequired;
        
            MiniMapper.Map(_customerSettings, model);

            model.UsernamesEnabled = _customerSettings.CustomerLoginType != CustomerLoginType.Email;
            model.CheckUsernameAvailabilityEnabled = _customerSettings.CheckUsernameAvailabilityEnabled;
            model.DisplayCaptcha = _captchaSettings.CanDisplayCaptcha && _captchaSettings.ShowOnRegistrationPage;

            ViewBag.AvailableTimeZones = new List<SelectListItem>();
            foreach (var tzi in _dateTimeHelper.GetSystemTimeZones())
            {
                ViewBag.AvailableTimeZones.Add(new SelectListItem { Text = tzi.DisplayName, Value = tzi.Id, Selected = (tzi.Id == _dateTimeHelper.DefaultStoreTimeZone.Id) });
            }

            if (_customerSettings.CountryEnabled)
            {
                await AddCountriesAndStatesToViewBagAsync(model.CountryId, _customerSettings.StateProvinceEnabled, model.StateProvinceId ?? 0);
            }
        }

        /// <summary>
        /// Assigns customer roles, publishes an event, sends email messages, signs the customer in depending on configuration & returns appropriate redirect.
        /// </summary>
        private async Task<IActionResult> FinalizeCustomerRegistrationAsync(Customer customer, string returnUrl)
        {
            await AssignCustomerRolesAsync(customer);

            // Add reward points for customer registration (if enabled).
            if (_rewardPointsSettings.Enabled && _rewardPointsSettings.PointsForRegistration > 0)
            {
                customer.AddRewardPointsHistoryEntry(_rewardPointsSettings.PointsForRegistration, T("RewardPoints.Message.RegisteredAsCustomer"));
            }

            await Services.EventPublisher.PublishAsync(new CustomerRegisteredEvent { Customer = customer });

            // Notifications
            if (_customerSettings.NotifyNewCustomerRegistration)
            {
                await _messageFactory.SendCustomerRegisteredNotificationMessageAsync(customer, _localizationSettings.DefaultAdminLanguageId);
            }

            switch (_customerSettings.UserRegistrationType)
            {
                case UserRegistrationType.EmailValidation:
                {
                    // Send an email with generated token.
                    var code = await _userManager.GenerateEmailConfirmationTokenAsync(customer);

                    customer.GenericAttributes.AccountActivationToken = code;
                    await _db.SaveChangesAsync();
                    await _messageFactory.SendCustomerEmailValidationMessageAsync(customer, Services.WorkContext.WorkingLanguage.Id);

                    return RedirectToRoute("RegisterResult", new { resultId = (int)UserRegistrationType.EmailValidation });
                }
                case UserRegistrationType.AdminApproval:
                {
                    return RedirectToRoute("RegisterResult", new { resultId = (int)UserRegistrationType.AdminApproval });
                }
                case UserRegistrationType.Standard:
                {
                    // Send customer welcome message.
                    await _messageFactory.SendCustomerWelcomeMessageAsync(customer, Services.WorkContext.WorkingLanguage.Id);
                    await _signInManager.SignInAsync(customer, isPersistent: false);

                    var redirectUrl = Url.RouteUrl("RegisterResult", new { resultId = (int)UserRegistrationType.Standard });
                    if (returnUrl.HasValue())
                    {
                        redirectUrl = _webHelper.ModifyQueryString(redirectUrl, "returnUrl=" + HttpUtility.UrlEncode(returnUrl), null);
                    }
                            
                    return Redirect(redirectUrl);
                }
                default:
                {
                    return RedirectToRoute("Homepage");
                }
            }
        }

        private async Task MapRegisterModelToCustomerAsync(Customer customer, RegisterModel model)
        {
            // Properties
            if (_dateTimeSettings.AllowCustomersToSetTimeZone)
            {
                customer.TimeZoneId = model.TimeZoneId;
            }

            // VAT number
            if (_taxSettings.EuVatEnabled)
            {
                customer.GenericAttributes.VatNumber = model.VatNumber;

                var vatCheckResult = await _taxService.GetVatNumberStatusAsync(model.VatNumber);
                customer.VatNumberStatusId = (int)vatCheckResult.Status;

                // Send VAT number admin notification.
                if (model.VatNumber.HasValue() && _taxSettings.EuVatEmailAdminWhenNewVatSubmitted)
                {
                    await _messageFactory.SendNewVatSubmittedStoreOwnerNotificationAsync(customer, model.VatNumber, vatCheckResult.Address, _localizationSettings.DefaultAdminLanguageId);
                }
            }

            // Form fields
            customer.FirstName = model.FirstName;
            customer.LastName = model.LastName;

            if (_customerSettings.CompanyEnabled)
            {
                customer.Company = model.Company;
            }
            
            if (_customerSettings.DateOfBirthEnabled)
            {
                try
                {
                    customer.BirthDate = new DateTime(model.DateOfBirthYear.Value, model.DateOfBirthMonth.Value, model.DateOfBirthDay.Value);
                }
                catch 
                { 
                }
            }

            if (_customerSettings.CustomerNumberMethod == CustomerNumberMethod.AutomaticallySet && customer.CustomerNumber.IsEmpty())
            {
                customer.CustomerNumber = customer.Id.Convert<string>();
            }
            if (_customerSettings.GenderEnabled)
            {
                customer.Gender = model.Gender;
            }
            if (_customerSettings.ZipPostalCodeEnabled)
            {
                customer.GenericAttributes.ZipPostalCode = model.ZipPostalCode;
            }
            if (_customerSettings.CountryEnabled)
            {
                customer.GenericAttributes.CountryId = model.CountryId;
            }
            if (_customerSettings.StreetAddressEnabled)
            {
                customer.GenericAttributes.StreetAddress = model.StreetAddress;
            }
            if (_customerSettings.StreetAddress2Enabled)
            {
                customer.GenericAttributes.StreetAddress2 = model.StreetAddress2;
            }
            if (_customerSettings.CityEnabled)
            {
                customer.GenericAttributes.City = model.City;
            }
            if (_customerSettings.CountryEnabled && _customerSettings.StateProvinceEnabled)
            {
                customer.GenericAttributes.StateProvinceId = model.StateProvinceId;
            }
            if (_customerSettings.PhoneEnabled)
            {
                customer.GenericAttributes.Phone = model.Phone;
            }
            if (_customerSettings.FaxEnabled)
            {
                customer.GenericAttributes.Fax = model.Fax;
            }
            
            // Newsletter subscription
            if (_customerSettings.NewsletterEnabled && model.Newsletter)
            {
                var subscription = await _db.NewsletterSubscriptions
                    .ApplyMailAddressFilter(customer.Email, Services.StoreContext.CurrentStore.Id)
                    .FirstOrDefaultAsync();

                if (subscription != null)
                {
                    subscription.Active = true;   
                }
                else
                {
                    subscription = new NewsletterSubscription
                    {
                        NewsletterSubscriptionGuid = Guid.NewGuid(),
                        Email = customer.Email,
                        Active = true,
                        CreatedOnUtc = DateTime.UtcNow,
                        StoreId = Services.StoreContext.CurrentStore.Id,
                        WorkingLanguageId = Services.WorkContext.WorkingLanguage.Id
                    };

                    _db.NewsletterSubscriptions.Add(subscription);
                }

                await _db.SaveChangesAsync();
            }
            
            // Insert default address (if possible).
            var defaultAddress = new Address
            {
                Title = customer.Title,
                FirstName = customer.FirstName,
                LastName = customer.LastName,
                Email = customer.Email,
                Company = customer.Company,
                CountryId = customer.GenericAttributes.CountryId,
                ZipPostalCode = customer.GenericAttributes.ZipPostalCode,
                StateProvinceId = customer.GenericAttributes.StateProvinceId,
                City = customer.GenericAttributes.City,
                Address1 = customer.GenericAttributes.StreetAddress,
                Address2 = customer.GenericAttributes.StreetAddress2,
                PhoneNumber = customer.GenericAttributes.Phone,
                FaxNumber = customer.GenericAttributes.Fax,
                CreatedOnUtc = customer.CreatedOnUtc
            };

            if (await _addressService.IsAddressValidAsync(defaultAddress))
            {
                // Set default addresses.
                customer.Addresses.Add(defaultAddress);
                customer.BillingAddress = defaultAddress;
                customer.ShippingAddress = defaultAddress;
            }

            _db.TryUpdate(customer);
            await _db.SaveChangesAsync();
        }

        private async Task AssignCustomerRolesAsync(Customer customer)
        {
            // Add customer to 'Registered' role.
            var registeredRole = await _db.CustomerRoles
                .Where(x => x.SystemName == SystemCustomerRoleNames.Registered)
                .FirstOrDefaultAsync();

            await _userManager.AddToRoleAsync(customer, registeredRole.Name);

            // Add customer to custom configured role.
            if (_customerSettings.RegisterCustomerRoleId != 0 && _customerSettings.RegisterCustomerRoleId != registeredRole.Id)
            {
                var customerRole = await _roleManager.FindByIdAsync(_customerSettings.RegisterCustomerRoleId);
                if (customerRole != null)
                {
                    await _userManager.AddToRoleAsync(customer, customerRole.Name);
                }
            }

            // Remove customer from 'Guests' role.
            var mappings = customer
                .CustomerRoleMappings
                .Where(x => !x.IsSystemMapping && x.CustomerRole.SystemName == SystemCustomerRoleNames.Guests)
                .Select(x => x.CustomerRole.Name)
                .ToList();

            await _userManager.RemoveFromRolesAsync(customer, mappings);
            await _db.SaveChangesAsync();
        }

        // TODO: (mh) (core) Find globally accessable place for this.
        private async Task AddCountriesAndStatesToViewBagAsync(int selectedCountryId, bool statesEnabled, int selectedStateId)
        {
            var availableCountries = new List<SelectListItem>
            {
                new SelectListItem { Text = T("Address.SelectCountry"), Value = "0" }
            };

            var countries = await _db.Countries
                .AsNoTracking()
                .ApplyStandardFilter()
                .ToListAsync();

            foreach (var c in countries)
            {
                availableCountries.Add(new SelectListItem
                {
                    Text = c.GetLocalized(x => x.Name),
                    Value = c.Id.ToString(),
                    Selected = c.Id == selectedCountryId
                });
            }

            ViewBag.AvailableCountries = availableCountries;

            if (statesEnabled)
            {
                var availableStates = new List<SelectListItem>();

                var states = await _db.StateProvinces
                    .AsNoTracking()
                    .ApplyCountryFilter(selectedStateId)
                    .ToListAsync();

                if (states.Any())
                {
                    foreach (var s in states)
                    {
                        availableStates.Add(new SelectListItem
                        {
                            Text = s.GetLocalized(x => x.Name),
                            Value = s.Id.ToString(),
                            Selected = s.Id == selectedStateId
                        });
                    }
                }
                else
                {
                    availableStates.Add(new SelectListItem { Text = T("Address.OtherNonUS"), Value = "0" });
                }

                ViewBag.AvailableStates = availableStates;
            }
        }

        private void AddErrors(IdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
        }

        private IActionResult RedirectToLocal(string returnUrl)
        {
            return RedirectToReferrer(returnUrl, () => RedirectToRoute("Login"));
        }

        #endregion
    }
}