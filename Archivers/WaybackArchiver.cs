using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

/* Summary:
 Internet Archive "Save Page Now" (web.archive.org), Otherwise known as "The Wayback Machine"
*/
public class WaybackArchiver : IArchiver
{
    public string Key => "wayback";
    public string Name => "Wayback Machine";

    // A finished capture lives at /web/<14-digit timestamp>/<original url>
    private static readonly Regex Snapshot = new(@"^https?://web\.archive\.org/web/\d{8,14}[a-z_]*/", RegexOptions.Compiled);

    public async Task<ArchiveResult> SaveAsync(IPage page, string url, ArchiveOptions options, CancellationToken ct)
    {
        var timeout = options.TimeoutSeconds * 1000;
        try
        {
            // Visiting /save/<url> triggers a capture, then redirects to the snapshot.
            await page.GotoAsync("https://web.archive.org/save/" + url,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });

            await page.WaitForURLAsync(Snapshot, new PageWaitForURLOptions { Timeout = timeout });
            return new ArchiveResult(Name, true, page.Url, "Saved.");
        }
        catch (Exception ex) when (PageHelpers.IsTimeout(ex))
        {
            await PageHelpers.ScreenshotAsync(page, options, Key);

            // Sometimes the capture is queued but the page keeps showing progress; report what we see.
            var body = await SafeBodyTextAsync(page);
            if (body.Contains("Job failed", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("error", StringComparison.OrdinalIgnoreCase))
                return new ArchiveResult(Name, false, null, "Save Page Now reported an error (see screenshot if enabled).");

            return new ArchiveResult(Name, false, null,
                $"Timed out after {options.TimeoutSeconds}s; the capture may still be processing. Last URL: {page.Url}");
        }
        catch (PlaywrightException ex)
        {
            await PageHelpers.ScreenshotAsync(page, options, Key);
            return new ArchiveResult(Name, false, null, ex.Message.Split('\n')[0]);
        }
    }

    private static async Task<string> SafeBodyTextAsync(IPage page)
    {
        try { return await page.InnerTextAsync("body", new PageInnerTextOptions { Timeout = 2000 }); }
        catch (PlaywrightException) { return string.Empty; }
    }
}
