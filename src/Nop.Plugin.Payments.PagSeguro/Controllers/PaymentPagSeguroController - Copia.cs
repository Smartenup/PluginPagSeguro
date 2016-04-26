using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.PagSeguro.Models;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Stores;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Security;
using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Mvc;
using Uol.PagSeguro.Domain;
using Uol.PagSeguro.Exception;
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

        public PaymentPagSeguroController(IWorkContext workContext,
            IStoreService storeService, 
            ISettingService settingService, 
            IPaymentService paymentService, 
            IOrderService orderService, 
            IOrderProcessingService orderProcessingService,
            ILogger logger, 
            PaymentSettings paymentSettings, 
            ILocalizationService localizationService,
            PagSeguroPaymentSettings pagSeguroPaymentSettings)
        {
            this._workContext = workContext;
            this._storeService = storeService;
            this._settingService = settingService;
            this._orderService = orderService;
            this._orderProcessingService = orderProcessingService;
            this._logger = logger;
            this._localizationService = localizationService;
            this._pagSeguroPaymentSettings = pagSeguroPaymentSettings;

        }

        [AdminAuthorize]
        [ChildActionOnly]
        public ActionResult Configure()
        {
            //load settings for a chosen store scope
            var storeScope = this.GetActiveStoreScopeConfiguration(_storeService, _workContext);
            var pagSeguroPaymentSettings = _settingService.LoadSetting<PagSeguroPaymentSettings>(storeScope);


            var model = new ConfigurationModel();
            model.Email = pagSeguroPaymentSettings.Email;
            model.Token = pagSeguroPaymentSettings.Token;

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
            var storeScope = this.GetActiveStoreScopeConfiguration(_storeService, _workContext);
            var pagSeguroPaymentSettings = _settingService.LoadSetting<PagSeguroPaymentSettings>(storeScope);


            //save settings
            pagSeguroPaymentSettings.Email = model.Email;
            pagSeguroPaymentSettings.Token = model.Token;

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
        /*
        [NonAction]
        public ActionResult PaymentReturn()
        {
            _logger.Error("PaymentReturn - chamada inválida.");
            return Content("");
        }

        /// <summary>
        /// PaymentReturn utilizando API de notificação automática do PagSeguro
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [ValidateInput(false)]
        public ActionResult PaymentReturn(FormCollection model)
        {
            Order _order = new Order();

            try
            {
                DateTime utcNow = DateTime.UtcNow;
                
                if (model == null)
                {
                    _logger.Error("PaymentReturn - FormCollection is null.");
                    return null;
                }

                if (model.Count == 0)
                {
                    _logger.Error("PaymentReturn - FormCollection count is zero.");
                    return null;
                }

                if (string.IsNullOrEmpty(model["Referencia"]))
                {
					_logger.Error("PaymentReturn - Referencia esta em branco.");
                    return null;
                }

                if (model["Referencia"].Length == 36)
                    _order = _orderService.GetOrderByGuid(new Guid(model["Referencia"]));
                else
                    _order = _orderService.GetOrderById(int.Parse(model["Referencia"]));


                string postMessage = string.Empty;
                foreach (var key in model.AllKeys)
                {
                    if (!string.IsNullOrEmpty(postMessage))
                        postMessage += "&";

                    postMessage += string.Format("{0}: {1}", key.ToString(), model[key]);
                }

                if (!string.IsNullOrEmpty(postMessage))
                    _logger.Information(string.Format("HttpPostMessage: ", postMessage));

                if (_order == null)
                {
                    _logger.Error("Pedido não encontrado. " + model["Referencia"]);
                    return null;
                }
               
                if (!string.IsNullOrEmpty(model["Anotacao"]))
                {
                    _order.OrderNotes.Add(new OrderNote()
                    {
                        Note = model["Anotacao"],
                        CreatedOnUtc = utcNow,
                        DisplayToCustomer = true
                    });
                }

                string tipoPagamento = model["TipoPagamento"];
                
                switch (model["StatusTransacao"])
                {
                    case "Completo":
                        _order.PaymentStatus = PaymentStatus.Authorized;
                        _orderProcessingService.MarkOrderAsPaid(_order);
                        break;
                    case "Aguardando Pagto":
                        _order.PaymentStatus = PaymentStatus.Pending;
                        break;
                    case "Aprovado":
                        _order.PaymentStatus = PaymentStatus.Paid;
                        _order.PaidDateUtc = DateTime.Now;
                        _orderProcessingService.MarkAsAuthorized(_order);
                        break;
                    case "Em Análise":
                        _order.PaymentStatus = PaymentStatus.Pending;
                        break;
                    case "Devolvido":
                        Decimal orderTotal = _order.OrderTotal;
                        Decimal num = _order.RefundedAmount + orderTotal;
                        _order.RefundedAmount = num;
                        _order.PaymentStatus = PaymentStatus.Refunded;
                        _order.PaidDateUtc = utcNow;
                        _order.OrderStatus = OrderStatus.Pending;
                        break;
    
                }

                if (model["StatusTransacao"] != "Cancelado")
                {
                    OrderNote note = new OrderNote();
                    note.CreatedOnUtc = utcNow;
                    note.DisplayToCustomer = true;
                    note.Note = string.Format("Forma de pagamento: {0}. {1}.", tipoPagamento, model["StatusTransacao"]);
                    _order.OrderNotes.Add(note);

                    this._orderService.UpdateOrder(_order);
                }

     
            }
            catch (Exception ex)
            {
                string erro = string.Format("PaymentReturn - {0} || {1} || {2} ", model.ToString(), model.AllKeys.ToString(), ex.Message.ToString());

                _logger.Error(erro);

                if (_order.OrderStatus != OrderStatus.Cancelled)
                    _orderProcessingService.CancelOrder(_order, true);
            }

            return Content("");
        }
        */


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
                    return Content("");
                }
                else
                    _logger.Information("PagSeguro - NotificationCode:" + notificationCode);


                //Monta as credenciais
                var email = _pagSeguroPaymentSettings.Email;
                var token = _pagSeguroPaymentSettings.Token;

                AccountCredentials credentials = new AccountCredentials(email, token);


                Order _order = null;


                StringBuilder transactionMessage = new StringBuilder();
                Transaction transactionPagSeguro = null;

                try
                {
                    transactionPagSeguro = NotificationService.CheckTransaction(credentials, notificationCode.Trim().ToUpper(), false);

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

                    return Content("");
                }

                //Transacao do pagseguro encontrada, portanto vamos achar o pedido               
                if (transactionPagSeguro.Reference.Length == 36)
                    _order = _orderService.GetOrderByGuid(new Guid(transactionPagSeguro.Reference));
                else
                    _order = _orderService.GetOrderById(int.Parse(transactionPagSeguro.Reference));


                if (_order == null)
                {
                    _logger.Information("Pedido não encontrado. Abortando. Pedido: " + transactionPagSeguro.Reference);
                    return Content("");
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
                        _order.PaymentStatus = PaymentStatus.Pending;
                        AddOrderNote("Aguardando pagamento.", true, ref _order);
                        AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, ref _order);
                        break;
                    case 2:
                        _order.PaymentStatus = PaymentStatus.Pending;
                        AddOrderNote("Em processamento pelo PagSeguro.", true, ref _order);
                        AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, ref _order);
                        break;
                    case 3:
                        _order.PaymentStatus = PaymentStatus.Authorized;
                        _orderProcessingService.MarkAsAuthorized(_order);
                        AddOrderNote("Pagamento aprovado.", true, ref _order);
                        AddOrderNote(string.Format("Forma de pagamento: {0}.", paymentMethodType), true, ref _order);
                        break;
                    case 4:
                        _order.PaymentStatus = PaymentStatus.Paid;
                        _order.PaidDateUtc = DateTime.UtcNow;
                        _orderProcessingService.MarkOrderAsPaid(_order);
                        AddOrderNote("Pagamento disponível para saque no PagSeguro.", false, ref _order);
                        break;
                    case 5:
                        _order.PaymentStatus = PaymentStatus.Voided;
                        _orderService.UpdateOrder(_order);
                        AddOrderNote("Em disputa: o comprador, dentro do prazo de liberação da transação, abriu uma disputa.", true, ref _order);
                        break;
                    case 6:
                        _order.PaymentStatus = PaymentStatus.Refunded;
                        _order.OrderStatus = OrderStatus.Cancelled;
                        _orderProcessingService.CancelOrder(_order, true);
                        AddOrderNote("Valor reembolsado para o comprador. Pedido Cancelado.", true, ref _order);
                        break;
                    case 7:
                        _order.PaymentStatus = PaymentStatus.Voided;
                        _order.OrderStatus = OrderStatus.Cancelled;
                        _orderProcessingService.CancelOrder(_order, true);
                        AddOrderNote("Transação Cancelada. Motivos: Expiração do prazo de pagamento ou cancelada pelo comprador.", true, ref _order);
                        break;
                }


            }
            catch (Exception ex)
            {
                string erro = string.Format("Erro PagSeguro - {0}", ex.Message.ToString());

                _logger.Error(erro, ex);
            }

            return Content("");
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
        private void AddOrderNote(string note, bool showNoteToCustomer, ref Order order)
        {
            OrderNote orderNote = new OrderNote();
            orderNote.CreatedOnUtc = DateTime.UtcNow;
            orderNote.DisplayToCustomer = showNoteToCustomer;
            orderNote.Note = note;
            order.OrderNotes.Add(orderNote);

            this._orderService.UpdateOrder(order);
        }
    }
}
