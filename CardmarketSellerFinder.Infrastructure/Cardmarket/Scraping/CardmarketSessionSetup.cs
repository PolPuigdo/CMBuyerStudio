using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Extensions;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Interaction;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Locators;
using CMBuyerStudio.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public sealed class CardmarketSessionSetup
    {
        private readonly ScrapingOptions _scrapingOptions;

        public CardmarketSessionSetup(IOptions<ScrapingOptions> scrapingOptions)
        {
            _scrapingOptions = scrapingOptions.Value;
        }

        public async Task PrepareAsync(IPage page, string url, CancellationToken cancellationToken = default)
        {
            await NavigateAsync(page, url, cancellationToken);
            await AcceptCookiesIfPresentAsync(page, cancellationToken);
            await SignInIfNeededAsync(page, cancellationToken);
        }

        private async Task NavigateAsync(IPage page, string url, CancellationToken cancellationToken)
        {
            await page.GoToAndWaitAsync(url, CardmarketLocators.Offers.Table);
        }

        private async Task AcceptCookiesIfPresentAsync(IPage page, CancellationToken cancellationToken)
        {
            await PlaywrightClicker.ClickWithJitterAsync(page, CardmarketLocators.Cookies.AcceptButton, waitAfterCliuckMs: 1000);
        }

        private async Task SignInIfNeededAsync(IPage page, CancellationToken cancellationToken)
        {
            var usernameInput = page.Locator(CardmarketLocators.Login.UsernameInput).First;
            await TypeIntoLoginInputAsync(usernameInput, _scrapingOptions.CardmarketUsername);

            var passwordInput = page.Locator(CardmarketLocators.Login.PasswordInput).First;
            await TypeIntoLoginInputAsync(passwordInput, _scrapingOptions.CardmarketPassword);

            var loginButton = page.Locator(CardmarketLocators.Login.SubmitButton).First;
            await PlaywrightClicker.ClickWithJitterAsync(loginButton);
        }

        private async Task TypeIntoLoginInputAsync(ILocator input, string value)
        {
            await Task.Delay(WaitTiming.GetRandom(130));
            await PlaywrightClicker.ClickAsync(input);
            await input.FillAsync(string.Empty);

            foreach (var character in value)
            {
                await input.PressSequentiallyAsync(character.ToString());
                await Task.Delay(WaitTiming.GetRandomTypingDelay());
            }
        }
    }
}
