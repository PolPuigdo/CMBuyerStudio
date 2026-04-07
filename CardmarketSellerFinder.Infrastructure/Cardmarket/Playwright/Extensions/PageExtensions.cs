using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Extensions
{
    public static class PageExtensions
    {
        public static async Task<IResponse?> GoToAndWaitAsync(
            this IPage page,
            string url,
            string? waitSelector = null,
            int waitMs = 500,
            int timeout = 30000)
        {
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeout
            });

            if (!string.IsNullOrWhiteSpace(waitSelector))
            {
                await page.WaitForSelectorAsync(waitSelector, new PageWaitForSelectorOptions
                {
                    Timeout = timeout
                });
            }

            await Task.Delay(WaitTiming.GetRandom(waitMs));

            return response;
        }
    }
}
