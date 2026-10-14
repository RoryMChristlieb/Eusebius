using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

/* Summary:
 archive.today (also reachable as archive.ph / archive.is / archive.md ...).
 This site often shows a captcha. Run with --headed so you can solve it by hand;
 the archiver waits for you until the timeout expires.
*/
public class ArchiveTodayArchiver : IArchiver
{
    public string Key => "archivetoday";
    public string Name => "archive.today";

    // Mirrors are tried in order if one is unreachable (they get blocked/moved regularly).
    private static readonly string[] Mirrors = { "archive.ph", "archive.is", "archive.md", "archive.today" };

    // Finished snapshot: https://archive.ph/AbCdE   (or /wip/AbCdE while still being captured)
    private static readonly Regex Snapshot = new(
        @"^https?://archive\.(ph|is|md|today|li|fo|vn)/(wip/)?[A-Za-z0-9]{4,10}/?$", RegexOptions.Compiled);

    public async Task<ArchiveResult> SaveAsync(IPage page, string url, ArchiveOptions options, CancellationToken ct)
    {
        var timeout = options.TimeoutSeconds * 1000;
        string? lastError = null;

        foreach (var mirror in Mirrors)
        {
            try
            {
                await page.GotoAsync($"https://{mirror}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });

                var input = await PageHelpers.FirstVisibleAsync(page, "input#url", "input[name='url']", "input[type='text']");
                if (input is null)
                {
                    lastError = $"{mirror}: submit form not found (captcha or block page?)";
                    if (await PageHelpers.LooksLikeCaptchaAsync(page) && options.Headed)
                    {
                        Console.WriteLine($"[{Name}] Captcha shown. Solve it in the browser window...");
                        input = await WaitForInputAsync(page, timeout);
                    }
                    if (input is null) continue;
                }

                await input.FillAsync(url);

                var submit = await PageHelpers.FirstVisibleAsync(page,
                    "form#submiturl input[type='submit']", "input[type='submit']", "button[type='submit']");
                if (submit is not null) await submit.ClickAsync();
                else await input.PressAsync("Enter");

                // Captcha may appear after submitting.
                if (await WaitForCaptchaOrResultAsync(page))
                {
                    if (!options.Headed)
                    {
                        await PageHelpers.ScreenshotAsync(page, options, Key);
                        return new ArchiveResult(Name, false, null,
                            "Captcha required. Re-run with --headed to solve it manually.");
                    }
                    Console.WriteLine($"[{Name}] Captcha shown. Solve it in the browser window...");
                }

                await page.WaitForURLAsync(Snapshot, new PageWaitForURLOptions { Timeout = timeout });

                // /wip/ means "work in progress"; wait for it to resolve to the final short link if possible.
                if (page.Url.Contains("/wip/"))
                {
                    try
                    {
                        await page.WaitForURLAsync(u => !u.Contains("/wip/"), new PageWaitForURLOptions { Timeout = timeout });
                    }
                    catch (Exception ex) when (PageHelpers.IsTimeout(ex)) { /* keep the wip link; still a valid pointer */ }
                }

                return new ArchiveResult(Name, true, page.Url, "Saved.");
            }
            catch (Exception ex) when (PageHelpers.IsTimeout(ex))
            {
                await PageHelpers.ScreenshotAsync(page, options, Key);
                lastError = $"{mirror}: timed out waiting for the snapshot. Last URL: {page.Url}";
                // A timeout after a successful submit is not a mirror problem; don't retry elsewhere.
                if (Regex.IsMatch(page.Url, @"archive\.[a-z]+/(submit|wip)")) break;
            }
            catch (PlaywrightException ex)
            {
                lastError = $"{mirror}: {ex.Message.Split('\n')[0]}";
            }
        }

        return new ArchiveResult(Name, false, null, lastError ?? "Unknown failure.");
    }

    private static async Task<ILocator?> WaitForInputAsync(IPage page, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var input = await PageHelpers.FirstVisibleAsync(page, "input#url", "input[name='url']");
            if (input is not null) return input;
            await Task.Delay(1000);
        }
        return null;
    }

    /* Summary:
     Waits briefly; returns true if a captcha appeared instead of progress.
    */
    private static async Task<bool> WaitForCaptchaOrResultAsync(IPage page)
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(1000);
            if (Snapshot.IsMatch(page.Url)) return false;
            if (await PageHelpers.LooksLikeCaptchaAsync(page)) return true;
        }
        return false;
    }
}
