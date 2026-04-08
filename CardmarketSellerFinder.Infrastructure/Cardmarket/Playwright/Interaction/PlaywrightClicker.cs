using CMBuyerStudio.Infrastructure.Cardmarket.Helpers;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Playwright.Interaction
{
    public static class PlaywrightClicker
    {
        public static async Task ClickWithJitterAsync(
        ILocator locator,
        int timeoutMs = 10000,
        int jitterPaddingPx = 4,
        int delayBeforeClickMs = 120,
        int waitAfterCliuckMs = 2000)
        {
            if (locator is null)
                throw new ArgumentNullException(nameof(locator));

            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = timeoutMs
            });

            if (!await locator.IsVisibleAsync())
                throw new InvalidOperationException("El elemento existe pero no es visible.");

            await locator.ScrollIntoViewIfNeededAsync();

            if (!await locator.IsVisibleAsync())
                throw new InvalidOperationException("El elemento no es visible después del scroll.");

            var box = await locator.BoundingBoxAsync();
            if (box is null)
                throw new InvalidOperationException("No se pudo obtener el BoundingBox del elemento.");

            // Evita que el jitter caiga demasiado en los bordes
            var safePaddingX = Math.Min(jitterPaddingPx, Math.Max(1, (int)(box.Width / 4)));
            var safePaddingY = Math.Min(jitterPaddingPx, Math.Max(1, (int)(box.Height / 4)));

            var minX = box.X + safePaddingX;
            var maxX = box.X + Math.Max(safePaddingX + 1, box.Width - safePaddingX);

            var minY = box.Y + safePaddingY;
            var maxY = box.Y + Math.Max(safePaddingY + 1, box.Height - safePaddingY);

            var clickX = NextDouble(minX, maxX);
            var clickY = NextDouble(minY, maxY);

            
            await Task.Delay(WaitTiming.GetRandom(delayBeforeClickMs));

            await locator.Page.Mouse.MoveAsync((float)clickX, (float)clickY);
            await locator.Page.Mouse.ClickAsync((float)clickX, (float)clickY);
            await Task.Delay(WaitTiming.GetRandom(waitAfterCliuckMs));
        }

        public static async Task ClickAsync(ILocator input)
        {
            ArgumentNullException.ThrowIfNull(input);

            await input.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible
            });
            await input.ScrollIntoViewIfNeededAsync();

            var box = await input.BoundingBoxAsync();
            if (box is null)
            {
                await input.ClickAsync();
                return;
            }

            var clickX = Math.Min(18d, Math.Max(1d, box.Width - 1d));
            var clickY = Math.Max(1d, box.Height / 2d);

            await input.ClickAsync(new LocatorClickOptions
            {
                Position = new Position
                {
                    X = (float)clickX,
                    Y = (float)clickY
                }
            });
        }

        public static async Task ClickWithJitterAsync(
        IPage page,
        string selector,
        int timeoutMs = 10000,
        int jitterPaddingPx = 4,
        int delayBeforeClickMs = 120,
        int waitAfterCliuckMs = 2000)
        {
            if (page is null)
                throw new ArgumentNullException(nameof(page));
            if (string.IsNullOrWhiteSpace(selector))
                throw new ArgumentException("El selector no puede ser nulo o vacío.", nameof(selector));

            var locator = page.Locator(selector);
            await ClickWithJitterAsync(locator, timeoutMs, jitterPaddingPx, delayBeforeClickMs, waitAfterCliuckMs);
        }

        private static double NextDouble(double min, double max)
        {
            if (max <= min)
                return min;

            return min + (Random.Shared.NextDouble() * (max - min));
        }
    }
}
