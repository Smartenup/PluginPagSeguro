using Nop.Web.Framework;
using Nop.Web.Framework.Mvc;

namespace Nop.Plugin.Payments.PagSeguro.Models
{
    public class ConfigurationModel : BaseNopModel
    {
        [NopResourceDisplayName("Plugins.Payments.PagSeguro.Fields.Email")]
        public string Email { get; set; }

        [NopResourceDisplayName("Plugins.Payments.PagSeguro.Fields.Token")]
        public string Token { get; set; }
    }
}