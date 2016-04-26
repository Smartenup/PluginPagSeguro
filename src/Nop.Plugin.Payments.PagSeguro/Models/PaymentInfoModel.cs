using Nop.Web.Framework.Mvc;

namespace Nop.Plugin.Payments.PagSeguro.Models
{
    public class PaymentInfoModel : BaseNopModel
    {
        public string Email { get; set; }
        public string Token { get; set; }
    }
}