using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

/* Summary:
 Arquivo.pt (Portuguese Web Archive) "ArchivePageNow" tool.
 Tool page: https://arquivo.pt/services/savepagenow
 Selectors here are deliberately loose because the page was not verified against live markup;
 if it stops working, run with --headed --screenshots and adjust the selectors below.
*/
public class ArquivoArchiver : IArchiver
{
    public string Key => "arquivo";
    public string Name => "Arquivo.pt";

    // Finished captures are replayed under arquivo.pt/wayback/<timestamp>/<url>
    private static readonly Regex Snapshot = new(@"^https?://arquivo\.pt/wayback/\d{8,14}", RegexOptions.Compiled);

    public async Task<ArchiveResult> SaveAsync(IPage page, string url, ArchiveOptions options, CancellationToken ct)
    {
        var timeout = options.TimeoutSeconds * 1000;
        try
        {
            await page.GotoAsync("https://arquivo.pt/services/savepagenow?l=en",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 45_000 });

            var input = await PageHelpers.FirstVisibleAsync(page,
                "input[name='url']",
                "input[type='url']",
                "input[placeholder*='http' i]",
                "input[type='text']");

            if (input is null)
            {
                await PageHelpers.ScreenshotAsync(page, options, Key);
                return new ArchiveResult(Name, false, null, "URL input not found on the ArchivePageNow page.");
            }

            await input.FillAsync(url);

            var submit = await PageHelpers.FirstVisibleAsync(page,
                "button[type='submit']",
                "input[type='submit']",
                "button:has-text('Archive')",
                "button:has-text('Save')",
                "button:has-text('Arquivar')");
            if (submit is not null) await submit.ClickAsync();
            else await input.PressAsync("Enter");

            // The tool records the page in a replay window; a successful capture lands on /wayback/<timestamp>/...
            // Fall back to success if the page navigated to any archived-looking URL.
            await page.WaitForURLAsync(u => Snapshot.IsMatch(u) || u.Contains("/wayback/") || u.Contains("/record/"),
                new PageWaitForURLOptions { Timeout = timeout });

            // Give the recorder a moment to finish loading sub-resources before we finish.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20_000 }); }
            catch (Exception ex) when (PageHelpers.IsTimeout(ex)) { }

            return new ArchiveResult(Name, true, page.Url,
                "Submitted. Arquivo.pt preserves the page as you browse it, and it may take time to become searchable.");
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
