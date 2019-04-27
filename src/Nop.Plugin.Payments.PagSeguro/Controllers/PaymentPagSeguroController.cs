using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
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
using SmartenUP.Core.Services;
using System;
using System.Collections.Generic;
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
        private readonly IOrderNoteService _orderNoteService;

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
            IShippingService shippingService,
            IOrderNoteService orderNoteService)
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
            _orderNoteService = orderNoteService;

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
            model.UtilizarAmbienteSandBox = pagSeguroPaymentSettings.UtilizarAmbienteSandBox;

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
            pagSeguroPaymentSettings.UtilizarAmbienteSandBox = model.UtilizarAmbienteSandBox;

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

                ConfigurarAmbienteExecucao();

                //Monta as credenciais
                var email = _pagSeguroPaymentSettings.Email;
                var token = _pagSeguroPaymentSettings.Token;

                var credentials = new AccountCredentials(email, token);

                Order order = null;

                var transactionMessage = new StringBuilder();
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
                        transactionMessage.AppendFormat("{0}-{1}", item.Code, item.Message);

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
                        _orderNoteService.AddOrderNote("Aguardando pagamento.", true, order);
                        _orderNoteService.AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, order);

                        break;
                    case 2:

                        _orderNoteService.AddOrderNote("Em processamento pelo PagSeguro.", true, order);
                        _orderNoteService.AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, order);

                        break;
                    case 3:
                        if (order.PaymentStatus == PaymentStatus.Pending)
                        {
                            order.PaymentStatus = PaymentStatus.Authorized;

                            _orderProcessingService.MarkAsAuthorized(order);

                            _orderNoteService.AddOrderNote("Pagamento aprovado.", true, order);
                            _orderNoteService.AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, order);
                            _orderNoteService.AddOrderNote("Aguardando Impressão - Excluir esse comentário ao imprimir", false, order);

                            if (_pagSeguroPaymentSettings.AdicionarNotaPrazoFabricaoEnvio)
                                _orderNoteService.AddOrderNote(_orderNoteService.GetOrdeNoteRecievedPayment(order, "PagSeguro"), true, order, true);

                        }
                        else if ((order.PaymentStatus == PaymentStatus.Voided))
                        {
                            order.PaymentStatus = PaymentStatus.Authorized;

                            _orderProcessingService.MarkAsAuthorized(order);

                            _orderNoteService.AddOrderNote("Disputa encerrada em favor do vendedor. Pagamento aprovado", true, order, true);
                        }
                        break;
                    case 4:
                        _orderProcessingService.MarkOrderAsPaid(order);
                        _orderNoteService.AddOrderNote("Pagamento disponível para saque no PagSeguro.", false, order);
                        break;
                    case 5:
                        order.PaymentStatus = PaymentStatus.Voided;
                        _orderService.UpdateOrder(order);
                        _orderNoteService.AddOrderNote("Em disputa: o comprador, dentro do prazo de liberação da transação, abriu uma disputa.", true, order, true);
                        break;
                    case 6:
                        order.PaymentStatus = PaymentStatus.Refunded;
                        order.OrderStatus = OrderStatus.Cancelled;

                        _orderProcessingService.CancelOrder(order, true);

                        _orderNoteService.AddOrderNote("Valor reembolsado para o comprador. Pedido Cancelado.", true, order, true);
                        
                        break;
                    case 7:

                        order.PaymentStatus = PaymentStatus.Voided;
                        order.OrderStatus = OrderStatus.Cancelled;

                        _orderProcessingService.CancelOrder(order, true);
                        _orderNoteService.AddOrderNote("Transação Cancelada. Motivos: Expiração do prazo de pagamento ou cancelada pelo comprador.", true, order, true);

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

        [ValidateInput(false)]
        public ActionResult RedirectOrder()
        {
            string transactionId = Request["transaction_id"];

            //Valida os dados recebidos e loga info
            if (string.IsNullOrEmpty(transactionId))
            {
                _logger.Error("Transaction Id PagSeguro recebida vazia. Abortando método RedirectOrder");

                return new HttpStatusCodeResult(HttpStatusCode.InternalServerError);
            }
            else
                _logger.Information("PagSeguro - transactionId:" + transactionId);

            ConfigurarAmbienteExecucao();

            //Monta as credenciais
            var email = _pagSeguroPaymentSettings.Email;
            var token = _pagSeguroPaymentSettings.Token;

            var credentials = new AccountCredentials(email, token);

            Order order = null;

            var transactionMessage = new StringBuilder();
            Transaction transaction = null;

            try
            {
                transaction = TransactionSearchService.SearchByCode(credentials, transactionId);

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
            if (transaction.Reference.Length == 36)
                order = _orderService.GetOrderByGuid(new Guid(transaction.Reference));
            else
                order = _orderService.GetOrderById(int.Parse(transaction.Reference));


            if (order == null)
            {
                _logger.Information("Pedido não encontrado. Abortando. Pedido: " + transaction.Reference);

                return new HttpStatusCodeResult(HttpStatusCode.InternalServerError);
            }


            return RedirectToRoute("CheckoutCompleted", new { orderId = order.Id });

        }

        private void ConfigurarAmbienteExecucao()
        {
            PagSeguroConfiguration.UrlXmlConfiguration = HttpRuntime.AppDomainAppPath + 
                "\\Plugins\\Payments.PagSeguro\\Configuration\\PagSeguroConfig.xml";

            EnvironmentConfiguration.ChangeEnvironment(_pagSeguroPaymentSettings.UtilizarAmbienteSandBox);
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
    }
}
