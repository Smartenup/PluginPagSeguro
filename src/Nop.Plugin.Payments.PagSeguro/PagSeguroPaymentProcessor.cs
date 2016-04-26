using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Routing;
using Nop.Core;
using Nop.Core.Domain;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Plugins;
using Nop.Plugin.Payments.PagSeguro.Controllers;
using Nop.Services.Catalog;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Payments;
using Nop.Services.Tax;
using Uol.PagSeguro.Constants;
using Uol.PagSeguro.Domain;
using Uol.PagSeguro.Exception;
using Address = Nop.Core.Domain.Common.Address;
using Currency = Uol.PagSeguro.Constants.Currency;
using PaymentMethodType = Nop.Services.Payments.PaymentMethodType;

namespace Nop.Plugin.Payments.PagSeguro
{
    public class PagSeguroPaymentProcessor : BasePlugin, IPaymentMethod
    {
        #region Ctor

        public PagSeguroPaymentProcessor(
            PagSeguroPaymentSettings pagSeguroPaymentSettings,
            ISettingService settingService,
            ITaxService taxService,
            IPriceCalculationService priceCalculationService,
            ICurrencyService currencyService,
            ICustomerService customerService,
            CurrencySettings currencySettings,
            IWebHelper webHelper,
            StoreInformationSettings storeInformationSettings,
            IAddressAttributeParser addressAttributeParser,
            IWorkContext workContext
            )
        {
            _pagSeguroPaymentSettings = pagSeguroPaymentSettings;
            _settingService = settingService;
            _taxService = taxService;
            _priceCalculationService = priceCalculationService;
            _currencyService = currencyService;
            _customerService = customerService;
            _currencySettings = currencySettings;
            _webHelper = webHelper;
            _storeInformationSettings = storeInformationSettings;
            _addressAttributeParser = addressAttributeParser;
            _workContext = workContext;
        }

        #endregion

        #region Fields

        private readonly PagSeguroPaymentSettings _pagSeguroPaymentSettings;
        private readonly ISettingService _settingService;
        private readonly ITaxService _taxService;
        private readonly IPriceCalculationService _priceCalculationService;
        private readonly ICurrencyService _currencyService;
        private readonly ICustomerService _customerService;
        private readonly CurrencySettings _currencySettings;
        private readonly IWebHelper _webHelper;
        private readonly StoreInformationSettings _storeInformationSettings;
        private readonly IAddressAttributeParser _addressAttributeParser;
        private readonly IWorkContext _workContext;

        private const int PHONE_WITHOUT_AREA_CODE_MAX_LENGTH = 9;
        private const int PHONE_ONLY_NUMBER_ONE_MAX_SIZE = 11;

        #endregion

        #region Default Methods

        /// <summary>
        ///     Processa pagamento
        /// </summary>
        /// <param name="processPaymentRequest"></param>
        /// <returns></returns>
        public ProcessPaymentResult ProcessPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();
            result.NewPaymentStatus = PaymentStatus.Pending;
            return result;
        }

        /// <summary>
        ///     PostProcessPayment utilizando API de pagamento do PagSeguro
        /// </summary>
        /// <param name="postProcessPaymentRequest"></param>
        public void PostProcessPayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            var returnToken = string.Empty;
            var credentials = new AccountCredentials(_pagSeguroPaymentSettings.Email, _pagSeguroPaymentSettings.Token);
            try
            {
                var payment = new PaymentRequest();


                //Esse campo sempre deve ser BRL. PagSeguro não trabalha com outras moedas
                payment.Currency = Currency.Brl;

                var tipoFrete = string.Empty;
                payment.Shipping = new Shipping();

                if (!string.IsNullOrEmpty(postProcessPaymentRequest.Order.ShippingMethod))
                {
                    switch (postProcessPaymentRequest.Order.ShippingMethod)
                    {
                        case "PAC":
                            tipoFrete = "PAC";
                            payment.Shipping.ShippingType = ShippingType.Pac;
                            break;
                        case "SEDEX":
                            tipoFrete = "SEDEX";
                            payment.Shipping.ShippingType = ShippingType.Sedex;
                            break;
                        case "SEDEX 10":
                            tipoFrete = "SEDEX 10";
                            payment.Shipping.ShippingType = ShippingType.Sedex;
                            break;
                        case "e-SEDEX":
                            tipoFrete = "e-SEDEX";
                            payment.Shipping.ShippingType = ShippingType.Sedex;
                            break;

                        default:
                            payment.Shipping.ShippingType = ShippingType.NotSpecified;
                            break;
                    }

                    payment.Shipping.Cost = decimal.Round(postProcessPaymentRequest.Order.OrderShippingInclTax, 2);
                }

                //Pega produtos no carrinho de compras
                var cartItems = postProcessPaymentRequest.Order.OrderItems;
                foreach (var item in cartItems)
                {
                    var productID = string.IsNullOrWhiteSpace(item.Product.Sku)
                        ? item.Product.Id.ToString()
                        : item.Product.Sku;

                    var productName = GetProcuctName(item);

                    productName = AddItemDescrition(productName, item);

                    payment.Items.Add(new Item(productID,
                        productName,
                        item.Quantity,
                        decimal.Round(item.UnitPriceInclTax, 2),
                        (long) item.Product.Weight));
                }

                payment.ExtraAmount = 0;

                //Desconto aplicado na ordem subtotal
                if (postProcessPaymentRequest.Order.OrderSubTotalDiscountExclTax > 0)
                {
                    var discount = postProcessPaymentRequest.Order.OrderSubTotalDiscountExclTax;
                    discount = Math.Round(discount, 2);
                    payment.ExtraAmount += discount;
                }

                //desconto fixo, dado por cupom, os descontos podem ser cumulativos
                if (postProcessPaymentRequest.Order.OrderDiscount > 0)
                {
                    var discount = postProcessPaymentRequest.Order.OrderDiscount;
                    discount = Math.Round(discount, 2);
                    payment.ExtraAmount += discount;
                }

                if (payment.ExtraAmount.HasValue)
                {
                    if (payment.ExtraAmount == 0)
                        payment.ExtraAmount = null;
                    else
                        payment.ExtraAmount = payment.ExtraAmount*-1;
                }


                //Campo que o transaction ID utilizado futuramente no retorno automático para identificar a compra
                payment.Reference = postProcessPaymentRequest.Order.Id.ToString();

                if (postProcessPaymentRequest.Order.ShippingAddressId.HasValue &&
                    postProcessPaymentRequest.Order.ShippingAddress != null)
                {
                    var number = string.Empty;
                    var complement = string.Empty;

                    GetCustomNumberAndComplement(postProcessPaymentRequest.Order.ShippingAddress.CustomAttributes,
                        out number, out complement);

                    payment.Shipping.Address = new Uol.PagSeguro.Domain.Address("BRA",
                        postProcessPaymentRequest.Order.ShippingAddress.StateProvince.Abbreviation,
                        postProcessPaymentRequest.Order.ShippingAddress.City,
                        EnsureNeiborhood(postProcessPaymentRequest.Order.ShippingAddress.Address2), //Bairro no endereço
                        RetouchPostalCode(postProcessPaymentRequest.Order.ShippingAddress.ZipPostalCode),
                        EnsureStreet(postProcessPaymentRequest.Order.ShippingAddress.Address1),
                        number, //Número esta no campo customizado novo
                        complement //Complemento esta no campo customizado novo
                        );
                }

                payment.Sender = new Sender(GetBillingShippingFullName(postProcessPaymentRequest.Order.BillingAddress),
                    postProcessPaymentRequest.Order.Customer.Email,
                    GetPhoneNumber(postProcessPaymentRequest.Order.BillingAddress)
                    );
                var paymentRedirectUri = payment.Register(credentials);

                if (paymentRedirectUri == null)
                    throw new NopException("Erro ao gerar transação no PagSeguro. Uri nula.");
                HttpContext.Current.Response.Redirect(paymentRedirectUri.ToString());
            }
            catch (PagSeguroServiceException exception)
            {
                if (exception.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new NopException(
                        "Unauthorized: please verify if the credentials used in the web service call are correct.\n");
                }
                var err = "Errors: ";
                foreach (var error in exception.Errors)
                {
                    err += error.Message + " ";
                }
                throw new NopException(err);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public decimal GetAdditionalHandlingFee()
        {
            return _pagSeguroPaymentSettings.ValorFrete;
        }

        public CapturePaymentResult Capture(CapturePaymentRequest capturePaymentRequest)
        {
            var result = new CapturePaymentResult();
            result.AddError("Capture method not supported");
            return result;
        }

        public RefundPaymentResult Refund(RefundPaymentRequest refundPaymentRequest)
        {
            var result = new RefundPaymentResult();
            result.AddError("Refund method not supported");
            return result;
        }

        public VoidPaymentResult Void(VoidPaymentRequest voidPaymentRequest)
        {
            var result = new VoidPaymentResult();
            result.AddError("Void method not supported");
            return result;
        }

        public ProcessPaymentResult ProcessRecurringPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();
            result.AddError("Recurring payment not supported");
            return result;
        }

        public CancelRecurringPaymentResult CancelRecurringPayment(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            var result = new CancelRecurringPaymentResult();
            result.AddError("Recurring payment not supported");
            return result;
        }

        public bool CanRePostProcessPayment(Order order)
        {
            if (order == null)
                throw new ArgumentNullException("bool CanRePostProcessPayment");

            //payment status should be Pending
            if (order.PaymentStatus != PaymentStatus.Pending)
                return false;

            //let's ensure that at least 1 minute passed after order is placed
            if ((DateTime.UtcNow - order.CreatedOnUtc).TotalMinutes < 1)
                return false;

            return true;
        }

        public void GetConfigurationRoute(out string actionName, out string controllerName,
            out RouteValueDictionary routeValues)
        {
            actionName = "Configure";
            controllerName = "PaymentPagSeguro";
            routeValues = new RouteValueDictionary
            {
                {"Namespaces", "Nop.Plugin.Payments.PagSeguro.Controllers"},
                {"area", null}
            };
        }

        public void GetPaymentInfoRoute(out string actionName, out string controllerName,
            out RouteValueDictionary routeValues)
        {
            actionName = "PaymentInfo";
            controllerName = "PaymentPagSeguro";
            routeValues = new RouteValueDictionary
            {
                {"Namespaces", "Nop.Plugin.Payments.PagSeguro.Controllers"},
                {"area", null}
            };
        }

        public bool HidePaymentMethod(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country
            return false;
        }

        #endregion

        #region Propriedades

        public Type GetControllerType()
        {
            return typeof (PaymentPagSeguroController);
        }

        public bool SupportCapture
        {
            get { return false; }
        }

        public bool SupportPartiallyRefund
        {
            get { return false; }
        }

        public bool SupportRefund
        {
            get { return false; }
        }

        public bool SupportVoid
        {
            get { return false; }
        }

        public bool SkipPaymentInfo
        {
            get { return false; }
        }

        public RecurringPaymentType RecurringPaymentType
        {
            get
            {
                return RecurringPaymentType.NotSupported;
                ;
            }
        }

        public PaymentMethodType PaymentMethodType
        {
            get { return PaymentMethodType.Redirection; }
        }

        public decimal GetAdditionalHandlingFee(IList<ShoppingCartItem> cart)
        {
            return decimal.Zero;
        }

        #endregion

        #region Metodos Privados

        private void GetCustomNumberAndComplement(string customAttributes, out string number, out string complement)
        {
            number = string.Empty;
            complement = string.Empty;

            if (!string.IsNullOrWhiteSpace(customAttributes))
            {
                var attributes = _addressAttributeParser.ParseAddressAttributes(customAttributes);

                for (var i = 0; i < attributes.Count; i++)
                {
                    var valuesStr = _addressAttributeParser.ParseValues(customAttributes, attributes[i].Id);

                    var attributeName = attributes[i].GetLocalized(a => a.Name, _workContext.WorkingLanguage.Id);

                    if (
                        attributeName.Equals("Número", StringComparison.InvariantCultureIgnoreCase) ||
                        attributeName.Equals("Numero", StringComparison.InvariantCultureIgnoreCase)
                        )
                    {
                        number = _addressAttributeParser.ParseValues(customAttributes, attributes[i].Id)[0];
                    }

                    if (attributeName.Equals("Complemento", StringComparison.InvariantCultureIgnoreCase))
                        complement = _addressAttributeParser.ParseValues(customAttributes, attributes[i].Id)[0];
                }
            }

            if (string.IsNullOrWhiteSpace(number))
                number = "--";

            if (string.IsNullOrWhiteSpace(complement))
                complement = "--";

            if (complement.Length > 40)
                complement = complement.Substring(0, 39);
        }

        private string EnsureStreet(string street)
        {
            var ensureStreet = street;

            if (string.IsNullOrWhiteSpace(ensureStreet))
            {
                return string.Empty;
            }

            if (ensureStreet.Length > 80)
            {
                ensureStreet = ensureStreet.Substring(0, 79);
            }

            return ensureStreet;
        }

        private string EnsureNeiborhood(string neiborhood)
        {
            var ensuredNeiborhood = neiborhood;

            if (string.IsNullOrWhiteSpace(ensuredNeiborhood))
            {
                return string.Empty;
            }

            if (ensuredNeiborhood.Length > 60)
            {
                ensuredNeiborhood = ensuredNeiborhood.Substring(0, 59);
            }

            return ensuredNeiborhood;
        }

        private string GetBillingShippingFullName(Address address)
        {
            if (address == null)
            {
                throw new ArgumentNullException("address");
            }
            var firstName = address.FirstName;
            var lastName = address.LastName;
            var stringWithTwoOrMoreSpace = "";

            if (!string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName))
            {
                stringWithTwoOrMoreSpace = string.Format("{0} {1}", firstName, lastName);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(firstName))
                {
                    stringWithTwoOrMoreSpace = firstName;
                }
                if (!string.IsNullOrWhiteSpace(lastName))
                {
                    stringWithTwoOrMoreSpace = lastName;
                }
            }


            var billingShippingFullName = RemoveIncorrectSpaces(stringWithTwoOrMoreSpace);

            if (billingShippingFullName.Length > 50)
            {
                billingShippingFullName = billingShippingFullName.Substring(0, 50);
            }

            return billingShippingFullName;
        }

        private string AddItemDescrition(string productName, OrderItem item)
        {
            var attributeDescription = item.AttributeDescription;
            if (!string.IsNullOrWhiteSpace(attributeDescription))
            {
                attributeDescription = Regex.Replace(attributeDescription, @"<(.|\n)*?>", " - ");
            }
            productName = string.IsNullOrWhiteSpace(attributeDescription)
                ? productName
                : productName + " - " + attributeDescription;
            if (productName.Length > 100)
            {
                return productName.Substring(0, 99);
            }
            return productName;
        }

        private Phone GetPhoneNumber(Address address)
        {
            var areaCode = string.Empty;
            var number = string.Empty;

            var phoneNumber = GetOnlyNumbers(address.PhoneNumber);

            phoneNumber = RemoveIncorrectFoneAreaCodes(phoneNumber);

            ///Se o numero de telefone não conter o código de área
            if (phoneNumber.Length <= PHONE_WITHOUT_AREA_CODE_MAX_LENGTH)
            {
                number = phoneNumber;
            }
            ///Se o numero de telefone conter o código de área e for menor que o tamanho maximo para um telefone
            else if ((phoneNumber.Length > PHONE_WITHOUT_AREA_CODE_MAX_LENGTH) &&
                     (phoneNumber.Length <= PHONE_ONLY_NUMBER_ONE_MAX_SIZE))
            {
                areaCode = phoneNumber.Substring(0, 2);
                number = phoneNumber.Substring(2);
            }

            return new Phone(areaCode, number);
        }

        private string RemoveIncorrectFoneAreaCodes(string completeFoneNumber)
        {
            if (completeFoneNumber.Length > PHONE_WITHOUT_AREA_CODE_MAX_LENGTH)
            {
                if (completeFoneNumber.StartsWith("0"))
                {
                    completeFoneNumber = completeFoneNumber.Substring(1);
                }

                ///TODO:Validar os codigo de áreas validos e caso não for retirar da do telefone
            }


            return completeFoneNumber;
        }

        private string GetProcuctName(OrderItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Product.Name))
            {
                return item.Product.Name;
            }
            return "Nome não especificado";
        }

        private string RemoveDot(string stringWithDot) => stringWithDot.Replace(".", string.Empty);

        private string RemoveIncorrectSpaces(string stringWithTwoOrMoreSpace)
        {
            var str = string.Empty;
            for (var i = 0; i < stringWithTwoOrMoreSpace.Length; i++)
                if (stringWithTwoOrMoreSpace[i] == ' ')
                {
                    if ((i + 1 < stringWithTwoOrMoreSpace.Length) && (stringWithTwoOrMoreSpace[i + 1] != ' '))
                    {
                        str = str + stringWithTwoOrMoreSpace[i];
                    }
                }
                else
                    str = str + stringWithTwoOrMoreSpace[i];
            return str.Trim();
        }

        private string RemoveSpace(string stringWithSpace)
        {
            return stringWithSpace.Replace(" ", string.Empty).Trim();
        }

        private string RetouchPostalCode(string postalCode)
        {
            var stringOnlyNumbers = GetOnlyNumbers(postalCode);

            if (stringOnlyNumbers.Length > 8)
            {
                stringOnlyNumbers = stringOnlyNumbers.Substring(0, 8);
            }

            return stringOnlyNumbers;
        }

        private string GetOnlyNumbers(string stringValue)
        {
            var r = new Regex(@"\d+");

            var result = "";
            foreach (Match m in r.Matches(stringValue))
                result += m.Value;

            return result;
        }

        #endregion
    }
}