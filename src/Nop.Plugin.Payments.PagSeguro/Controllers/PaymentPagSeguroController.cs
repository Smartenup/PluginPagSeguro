using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.Shipping;
using Nop.Plugin.Payments.PagSeguro.Models;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Messages;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Shipping;
using Nop.Services.Stores;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Mvc;
using Uol.PagSeguro.Domain;
using Uol.PagSeguro.Exception;
using Uol.PagSeguro.Resources;
using Uol.PagSeguro.Service;

namespace Nop.Plugin.Payments.PagSeguro.Controllers
{
    public class PaymentPagSeguroController : BasePaymentController
    {
        private readonly IWorkContext _workContext;
        private readonly IStoreService _storeService;
        private readonly ISettingService _settingService;
        private readonly IOrderService _orderService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly ILogger _logger;
        private readonly PagSeguroPaymentSettings _pagSeguroPaymentSettings;
        private readonly ILocalizationService _localizationService;
        private readonly IWorkflowMessageService _workflowMessageService;
        private readonly IShippingService _shippingService;

        public PaymentPagSeguroController(IWorkContext workContext,
            IStoreService storeService, 
            ISettingService settingService, 
            IPaymentService paymentService, 
            IOrderService orderService, 
            IOrderProcessingService orderProcessingService,
            ILogger logger, 
            PaymentSettings paymentSettings, 
            ILocalizationService localizationService,
            PagSeguroPaymentSettings pagSeguroPaymentSettings,
            IWorkflowMessageService workflowMessageService,
            IShippingService shippingService)
        {
            _workContext = workContext;
            _storeService = storeService;
            _settingService = settingService;
            _orderService = orderService;
            _orderProcessingService = orderProcessingService;
            _logger = logger;
            _localizationService = localizationService;
            _pagSeguroPaymentSettings = pagSeguroPaymentSettings;
            _workflowMessageService = workflowMessageService;
            _shippingService = shippingService;

        }

        [AdminAuthorize]
        [ChildActionOnly]
        public ActionResult Configure()
        {
            //load settings for a chosen store scope
            var storeScope = GetActiveStoreScopeConfiguration(_storeService, _workContext);
            var pagSeguroPaymentSettings = _settingService.LoadSetting<PagSeguroPaymentSettings>(storeScope);
            if (pagSeguroPaymentSettings == null) throw new ArgumentNullException(nameof(pagSeguroPaymentSettings));


            var model = new ConfigurationModel();
            model.Email = pagSeguroPaymentSettings.Email;
            model.Token = pagSeguroPaymentSettings.Token;
            model.AdicionarNotaPrazoFabricaoEnvio = pagSeguroPaymentSettings.AdicionarNotaPrazoFabricaoEnvio;

            return View("~/Plugins/Payments.PagSeguro/Views/PaymentPagSeguro/Configure.cshtml", model);
            
        }

        [HttpPost]
        [AdminAuthorize]
        [AdminAntiForgery]
        [ChildActionOnly]
        public ActionResult Configure(ConfigurationModel model)
        {
            if (!ModelState.IsValid)
                return Configure();

            //load settings for a chosen store scope
            var storeScope = GetActiveStoreScopeConfiguration(_storeService, _workContext);
            var pagSeguroPaymentSettings = _settingService.LoadSetting<PagSeguroPaymentSettings>(storeScope);


            //save settings
            pagSeguroPaymentSettings.Email = model.Email;
            pagSeguroPaymentSettings.Token = model.Token;
            pagSeguroPaymentSettings.AdicionarNotaPrazoFabricaoEnvio = model.AdicionarNotaPrazoFabricaoEnvio;

            _settingService.SaveSetting(pagSeguroPaymentSettings);

            //now clear settings cache
            _settingService.ClearCache();

            SuccessNotification(_localizationService.GetResource("Admin.Plugins.Saved"));

            return View("~/Plugins/Payments.PagSeguro/Views/PaymentPagSeguro/Configure.cshtml", model);
        }

        [NonAction]
        public override IList<string> ValidatePaymentForm(FormCollection form)
        {
            var warnings = new List<string>();
            return warnings;
        }

        [ChildActionOnly]
        public ActionResult PaymentInfo()
        {
            return View("~/Plugins/Payments.PagSeguro/Views/PaymentPagSeguro/PaymentInfo.cshtml");
        }

        [NonAction]
        public override ProcessPaymentRequest GetPaymentInfo(FormCollection form)
        {
            var paymentInfo = new ProcessPaymentRequest();
            return paymentInfo;
        }


        /// <summary>
        /// PaymentReturn utilizando API de notificação automática do PagSeguro
        /// </summary>
        /// <param></param>
        /// <returns></returns>
        [ValidateInput(false)]
        public ActionResult PaymentReturn()
        {
            try
            {
                string notificationCode = Request["notificationCode"];

                //Valida os dados recebidos e loga info
                if (string.IsNullOrEmpty(notificationCode))
                {
                    _logger.Error("Notificação PagSeguro recebida vazia. Abortando método ReturnPayment");

                    return new HttpStatusCodeResult(HttpStatusCode.InternalServerError);
                }
                else
                    _logger.Information("PagSeguro - NotificationCode:" + notificationCode);


                //Monta as credenciais
                var email = _pagSeguroPaymentSettings.Email;
                var token = _pagSeguroPaymentSettings.Token;

                PagSeguroConfiguration.UrlXmlConfiguration = HttpRuntime.AppDomainAppPath + "\\Plugins\\Payments.PagSeguro\\Configuration\\PagSeguroConfig.xml";

                AccountCredentials credentials = new AccountCredentials(email, token);

                Order order = null;

                StringBuilder transactionMessage = new StringBuilder();
                Transaction transactionPagSeguro = null;

                try
                {
                    transactionPagSeguro = NotificationService.CheckTransaction(credentials, notificationCode.Trim().ToUpper());

                    transactionMessage.AppendFormat("Tipo: {0} ", transactionPagSeguro.TransactionType);
                    transactionMessage.AppendFormat("Status: {0} ", transactionPagSeguro.TransactionStatus);
                    transactionMessage.AppendFormat("Pagamento Método: {0} ", transactionPagSeguro.PaymentMethod.PaymentMethodType);
                    transactionMessage.AppendFormat("Referencia: {0} ", transactionPagSeguro.Reference);
                    

                    _logger.Information(transactionMessage.ToString());
                }
                catch (PagSeguroServiceException ex)
                {
                    transactionMessage.AppendFormat("PagSeguro erro {0}", ex.Message);

                    foreach (var item in ex.Errors)
                    {
                        transactionMessage.AppendFormat("{0}-{1}", item.Code, item.Message);
                    }

                    _logger.Error(transactionMessage.ToString(), ex);

                    return new HttpStatusCodeResult(HttpStatusCode.InternalServerError);
                }

                //Transacao do pagseguro encontrada, portanto vamos achar o pedido               
                if (transactionPagSeguro.Reference.Length == 36)
                    order = _orderService.GetOrderByGuid(new Guid(transactionPagSeguro.Reference));
                else
                    order = _orderService.GetOrderById(int.Parse(transactionPagSeguro.Reference));


                if (order == null)
                {
                    _logger.Information("Pedido não encontrado. Abortando. Pedido: " + transactionPagSeguro.Reference);

                    return new HttpStatusCodeResult(HttpStatusCode.InternalServerError);
                }

                string paymentMethodType = GetPaymentDescription(transactionPagSeguro);


                switch (transactionPagSeguro.TransactionStatus)
                {
                    //Código de status - Significado
                    //1	Aguardando pagamento: o comprador iniciou a transação, mas até o momento o PagSeguro não recebeu nenhuma informação sobre o pagamento.
                    //2	Em análise: o comprador optou por pagar com um cartão de crédito e o PagSeguro está analisando o risco da transação.
                    //3	Paga: a transação foi paga pelo comprador e o PagSeguro já recebeu uma confirmação da instituição financeira responsável pelo processamento.
                    //4	Disponível: a transação foi paga e chegou ao final de seu prazo de liberação sem ter sido retornada e sem que haja nenhuma disputa aberta.
                    //5	Em disputa: o comprador, dentro do prazo de liberação da transação, abriu uma disputa.
                    //6	Devolvida: o valor da transação foi devolvido para o comprador.
                    //7	Cancelada: a transação foi cancelada sem ter sido finalizada.

                    case 1:
                        order.PaymentStatus = PaymentStatus.Pending;
                        AddOrderNote("Aguardando pagamento.", true, ref order);
                        AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, ref order);
                        break;
                    case 2:
                        order.PaymentStatus = PaymentStatus.Pending;
                        AddOrderNote("Em processamento pelo PagSeguro.", true, ref order);
                        AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, ref order);
                        break;
                    case 3:
                        order.PaymentStatus = PaymentStatus.Authorized;
                        _orderProcessingService.MarkAsAuthorized(order);
                        AddOrderNote("Pagamento aprovado.", true, ref order);
                        AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, ref order);
                        AddOrderNote("Aguardando Impressão - Excluir esse comentário ao imprimir ", false, ref order);
                        if (_pagSeguroPaymentSettings.AdicionarNotaPrazoFabricaoEnvio)
                            AddOrderNote(GetOrdeNoteRecievedPayment(order), true, ref order, true);
                        break;
                    case 4:
                        _orderProcessingService.MarkOrderAsPaid(order);
                        AddOrderNote("Pagamento disponível para saque no PagSeguro.", false, ref order);
                        break;
                    case 5:
                        order.PaymentStatus = PaymentStatus.Voided;
                        _orderService.UpdateOrder(order);
                        AddOrderNote("Em disputa: o comprador, dentro do prazo de liberação da transação, abriu uma disputa.", true, ref order, true);
                        break;
                    case 6:
                        order.PaymentStatus = PaymentStatus.Refunded;
                        order.OrderStatus = OrderStatus.Cancelled;
                        _orderProcessingService.CancelOrder(order, true);
                        AddOrderNote("Valor reembolsado para o comprador. Pedido Cancelado.", true, ref order, true);
                        break;
                    case 7:
                        order.PaymentStatus = PaymentStatus.Voided;
                        order.OrderStatus = OrderStatus.Cancelled;
                        _orderProcessingService.CancelOrder(order, true);
                        AddOrderNote("Transação Cancelada. Motivos: Expiração do prazo de pagamento ou cancelada pelo comprador.", true, ref order);
                        break;
                }


            }
            catch (Exception ex)
            {
                string erro = string.Format("Erro PagSeguro - {0}", ex.Message.ToString());

                _logger.Error(erro, ex);

                return new HttpStatusCodeResult(HttpStatusCode.InternalServerError);
            }

            return new HttpStatusCodeResult(HttpStatusCode.OK); ;
        }


        [NonAction]
        private string GetOrdeNoteRecievedPayment(Nop.Core.Domain.Orders.Order order)
        {
            Nop.Core.Domain.Orders.OrderItem orderItem;
            int? biggestAmountDays;

            DeliveryDate biggestDeliveryDate = GetBiggestDeliveryDate(order, out biggestAmountDays, out orderItem);

            DateTime dateShipment = DateTime.Now.AddWorkDays(biggestAmountDays.Value);

            var str = new StringBuilder();

            str.AppendLine("Recebemos a liberação do pagamento pelo PagSeguro e será dado andamento no seu pedido.");
            str.AppendLine();
            str.AppendFormat("Lembramos que o maior prazo é da fabricante {0} de {1}",
                            orderItem.Product.ProductManufacturers.FirstOrDefault().Manufacturer.Name,
                            biggestDeliveryDate.GetLocalized(dd => dd.Name));
            str.AppendLine();
            str.AppendLine();
            str.AppendLine("*OBS: Caso o seu pedido tenha produtos com prazos diferentes, o prazo de entrega a ser considerado será o maior.");
            str.AppendLine();


            str.AppendFormat("Data máxima para postar nos correios: {0}", dateShipment.ToString("dd/MM/yyyy"));
            str.AppendLine();

            if (order.ShippingMethod.Contains("PAC") || order.ShippingMethod.Contains("SEDEX"))
            {
                try
                {
                    var shippingOption = _shippingService.GetShippingOption(order);

                    str.AppendFormat("Correios: {0} - {1} após a postagem", shippingOption.Name, shippingOption.Description);
                    str.AppendLine();
                }
                catch (Exception ex)
                {
                    _logger.Error("Erro no calculo do frete pela ordem", ex);
                }
                finally
                {
                    str.AppendLine();
                }
            }

            return str.ToString();

        }

        [NonAction]
        private DeliveryDate GetBiggestDeliveryDate(Nop.Core.Domain.Orders.Order order, out int? biggestAmountDays,
    out Nop.Core.Domain.Orders.OrderItem orderItem)
        {

            DeliveryDate deliveryDate = null;

            biggestAmountDays = 0;

            orderItem = null;

            foreach (var item in order.OrderItems)
            {
                var deliveryDateItem = _shippingService.GetDeliveryDateById(item.Product.DeliveryDateId);

                string deliveryDateText = deliveryDateItem.GetLocalized(dd => dd.Name);

                int? deliveryBigestInteger = GetBiggestInteger(deliveryDateText);

                if (deliveryBigestInteger.HasValue)
                {
                    if (deliveryBigestInteger.Value > biggestAmountDays)
                    {
                        biggestAmountDays = deliveryBigestInteger.Value;
                        deliveryDate = deliveryDateItem;
                        orderItem = item;
                    }
                }
            }


            return deliveryDate;
        }
        [NonAction]
        private int? GetBiggestInteger(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var integerResultsList = new List<int>();
            string integerSituation = string.Empty;
            int integerPosition = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (int.TryParse(text[i].ToString(), out integerPosition))
                {
                    integerSituation += text[i].ToString();
                }
                else
                {
                    if (!string.IsNullOrEmpty(integerSituation))
                    {
                        integerResultsList.Add(int.Parse(integerSituation));
                        integerSituation = string.Empty;
                    }
                }
            }

            int integerResult = 0;
            foreach (var item in integerResultsList)
            {
                if (item > integerResult)
                    integerResult = item;
            }


            return integerResult;
        }

       

        [NonAction]
        private string GetPaymentDescription(Transaction transactionPagSeguro)
        {
            string paymentMethodType = string.Empty;

            switch (transactionPagSeguro.PaymentMethod.PaymentMethodType)
            {
                case 1:
                    paymentMethodType = "Cartão de crédito";
                    break;
                case 2:
                    paymentMethodType = "Boleto";
                    break;
                case 3:
                    paymentMethodType = "Débito online (TEF)";
                    break;
                case 4:
                    paymentMethodType = "Saldo PagSeguro";
                    break;
                case 5:
                    paymentMethodType = "Oi Paggo";
                    break;
                case 7:
                    paymentMethodType = "Depósito em conta";
                    break;
            }

            return paymentMethodType;
        }

            [NonAction]
            //Adiciona anotaçoes ao pedido
            private void AddOrderNote(string note, bool showNoteToCustomer, ref Order order, bool sendEmail = false)
            {
                OrderNote orderNote = new OrderNote();
                orderNote.CreatedOnUtc = DateTime.UtcNow;
                orderNote.DisplayToCustomer = showNoteToCustomer;
                orderNote.Note = note;
                order.OrderNotes.Add(orderNote);

            _orderService.UpdateOrder(order);

            //new order notification
            if (sendEmail)
            {
                //email
                _workflowMessageService.SendNewOrderNoteAddedCustomerNotification(
                    orderNote, _workContext.WorkingLanguage.Id);
            }
        }
    }

    public static class DateTimeExtensions
    {
        public static DateTime AddWorkDays(this DateTime date, int workingDays)
        {
            return date.GetDates(workingDays < 0)
                .Where(newDate =>
                    (newDate.DayOfWeek != DayOfWeek.Saturday &&
                     newDate.DayOfWeek != DayOfWeek.Sunday &&
                     !newDate.IsHoliday()))
                .Take(Math.Abs(workingDays))
                .Last();
        }

        private static IEnumerable<DateTime> GetDates(this DateTime date, bool isForward)
        {
            while (true)
            {
                date = date.AddDays(isForward ? -1 : 1);
                yield return date;
            }
        }

        public static bool IsHoliday(this DateTime date)
        {
            return false;
        }
    }

}
