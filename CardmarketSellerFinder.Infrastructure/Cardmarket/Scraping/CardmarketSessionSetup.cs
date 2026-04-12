using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Extensions;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Interaction;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Locators;
using CMBuyerStudio.Application.Abstractions;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Scraping
{
    public sealed class CardmarketSessionSetup : ICardmarketSessionSetup
    {
        private readonly IAppSettingsService _appSettingsService;

        public CardmarketSessionSetup(IAppSettingsService appSettingsService)
        {
            _appSettingsService = appSettingsService;
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
            var settings = await _appSettingsService.GetCurrentAsync(cancellationToken);

            var usernameInput = page.Locator(CardmarketLocators.Login.UsernameInput).First;
            await TypeIntoLoginInputAsync(usernameInput, settings.Scraping.CardmarketUsername);

            var passwordInput = page.Locator(CardmarketLocators.Login.PasswordInput).First;
            await TypeIntoLoginInputAsync(passwordInput, settings.Scraping.CardmarketPassword);

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
