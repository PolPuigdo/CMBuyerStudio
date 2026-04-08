using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Locators
{
    public static class CardmarketLocators
    {
        public static class Cookies
        {
            public const string AcceptButton = "button[aria-label='Aceptar todas las cookies']";
        }

        public static class Login
        {
            public const string UsernameInput = "input[name='username']";
            public const string PasswordInput = "input[name='userPassword']";
            public const string SubmitButton = "input[type='submit'][class*='btn-outline-primary']";
        }

        public static class Offers
        {
            public const string Table = ".table.article-table";
            //public const string Table = ".article-table"; //TODO: NOT IN USE YET
            //public const string Rows = ".article-table tbody tr"; //TODO: NOT IN USE YET
            public const string OfferRow = ".article-table .table-body .article-row";
            public const string LoadMoreButton = "#loadMoreButton";
            public const string Price = ".col-offer .price-container span.color-primary";
        }
    }
}
