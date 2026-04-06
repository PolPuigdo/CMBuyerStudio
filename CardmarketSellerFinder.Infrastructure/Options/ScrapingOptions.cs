using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Options
{
    public sealed class ScrapingOptions
    {
        public const string SectionName = "Scraping";

        public bool Headless { get; set; } = false;

        public List<ProxyOptions> Proxies { get; set; } = new();

        public string SellerCountry { get; set; } = "1,2,3,35,5,6,8,9,11,12,7,14,15,16,17,21,19,20,22,23,25,26,27,31,30,10,28";
        public string Languages { get; set; } = "1";
        public int MinCondition { get; set; } = 2;
    }

    public sealed class ProxyOptions
    {
        public string Server { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
