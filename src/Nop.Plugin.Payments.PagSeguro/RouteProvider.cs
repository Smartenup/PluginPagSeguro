using Nop.Web.Framework.Mvc.Routes;
using System.Web.Mvc;
using System.Web.Routing;

namespace Nop.Plugin.Payments.PagSeguro
{
    public partial class RouteProvider : IRouteProvider
    {
        public void RegisterRoutes(RouteCollection routes)
        {
            routes.MapRoute("Plugin.Payments.PagSeguro.Configure",
                 "Plugins/PaymentPagSeguro/Configure",
                 new { controller = "PaymentPagSeguro", action = "Configure" },
                 new[] { "Nop.Plugin.Payments.PagSeguro.Controllers" }
            );

            routes.MapRoute("Plugin.Payments.PagSeguro.PaymentReturn",
                 "Plugins/PaymentPagSeguro/PaymentReturn",
                 new { controller = "PaymentPagSeguro", action = "PaymentReturn" },
                 new[] { "Nop.Plugin.Payments.PagSeguro.Controllers" }
            );
        }

        public int Priority
        {
            get { return 0; }
        }
    }
}