using Nop.Core.Configuration;

namespace Nop.Plugin.Payments.PagSeguro
{
    public class PagSeguroPaymentSettings : ISettings
    {
        public bool UseSandebox { get; set; }
        public string Email { get; set; }
        public string Token { get; set; }
        public decimal ValorFrete { get; set; }
        public string UrlPagSeguro { get; set; }
        public string UrlPagSeguroSandbox { get; set; }
    }
}