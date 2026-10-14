using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

/* Summary:
 GhostArchive (ghostarchive.org).
*/
public class GhostArchiveArchiver : IArchiver
{
    public string Key => "ghostarchive";
    public string Name => "GhostArchive";

    // Finished archives live under /archive/<id> (or /varchive/<id> for videos).
    private static readonly Regex Snapshot = new(@"^https?://ghostarchive\.org/(archive|varchive)/", RegexOptions.Compiled);

    public async Task<ArchiveResult> SaveAsync(IPage page, string url, ArchiveOptions options, CancellationToken ct)
    {
        var timeout = options.TimeoutSeconds * 1000;
        try
        {
            await page.GotoAsync("https://ghostarchive.org/",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });

            var input = await PageHelpers.FirstVisibleAsync(page,
                "form[action*='archive'] input[name='archive']",
                "input[name='archive']",
                "input[type='url']",
                "input[type='text']");

            if (input is null)
            {
                await PageHelpers.ScreenshotAsync(page, options, Key);
                return new ArchiveResult(Name, false, null, "Submit form not found (site layout changed or blocked).");
            }

            await input.FillAsync(url);

            var submit = await PageHelpers.FirstVisibleAsync(page,
                "form[action*='archive'] input[type='submit']",
                "form[action*='archive'] button",
                "input[type='submit']",
                "button[type='submit']");
            if (submit is not null) await submit.ClickAsync();
            else await input.PressAsync("Enter");

            await page.WaitForURLAsync(Snapshot, new PageWaitForURLOptions { Timeout = timeout });
            return new ArchiveResult(Name, true, page.Url, "Saved.");
        }
        catch (Exception ex) when (PageHelpers.IsTimeout(ex))
        {
            await PageHelpers.ScreenshotAsync(page, options, Key);
            return new ArchiveResult(Name, false, null,
                $"Timed out after {options.TimeoutSeconds}s. Last URL: {page.Url}");
        }
        catch (PlaywrightException ex)
        {
            await PageHelpers.ScreenshotAsync(page, options, Key);
            return new ArchiveResult(Name, false, null, ex.Message.Split('\n')[0]);
        }
    }
}
