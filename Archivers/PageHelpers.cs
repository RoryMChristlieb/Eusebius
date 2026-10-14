using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

internal static class PageHelpers
{
    /* Summary:
     Returns the first visible locator from the list of selectors, or null.
     Lets each archiver try a preferred selector and then fall back to looser ones,
     since these sites change their markup from time to time.
    */
    public static async Task<ILocator?> FirstVisibleAsync(IPage page, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                    return loc;
            }
            catch (PlaywrightException)
            {
                // Selector not usable right now; try the next one.
            }
        }
        return null;
    }

    /* Summary:
     True for Playwright timeouts. Depending on the version these surface either as
     System.TimeoutException or as a PlaywrightException whose message mentions the timeout.
    */
    public static bool IsTimeout(Exception ex) =>
        ex is TimeoutException ||
        (ex is PlaywrightException && ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase));

    /* Summary:
     True when the page shows a captcha / bot-check widget.
    */
    public static async Task<bool> LooksLikeCaptchaAsync(IPage page)
    {
        try
        {
            var frames = page.Locator(
                "iframe[src*='recaptcha'], iframe[src*='hcaptcha'], iframe[src*='turnstile'], " +
                "iframe[src*='challenges.cloudflare.com'], .g-recaptcha, .h-captcha, .cf-turnstile");
            return await frames.CountAsync() > 0;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    public static async Task ScreenshotAsync(IPage page, ArchiveOptions options, string key)
    {
        if (string.IsNullOrWhiteSpace(options.ScreenshotDir)) return;
        try
        {
            Directory.CreateDirectory(options.ScreenshotDir);
            var path = Path.Combine(options.ScreenshotDir, $"{key}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        }
        catch (PlaywrightException)
        {
            // Screenshots are diagnostic only; never fail the run because of them.
        }
    }
}
