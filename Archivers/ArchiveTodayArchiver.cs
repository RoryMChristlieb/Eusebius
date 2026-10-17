using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

/* Summary:
 archive.today (also reachable as archive.ph / archive.is / archive.md ...).

 If the page was already archived, archive.today shows a popup:
   "This page was last archived 1 month ago  [save]"
 This archiver reads that age. If the existing snapshot is at least
 <see cref="MaxReuseAge"/> old it clicks "save" to make a fresh one; otherwise it
 returns the link to the existing (recent) snapshot.

 The site also often shows a captcha. Run with --headed so you can solve it by hand;
 the archiver waits for you until the timeout expires.
*/ 
public class ArchiveTodayArchiver : IArchiver
{
    public string Key => "archivetoday";
    public string Name => "archive.today";

    // Snapshots younger than this are reused; this old or older triggers a new save.</summary>
    private static readonly TimeSpan MaxReuseAge = TimeSpan.FromHours(1);

    // Mirrors are tried in order if one is unreachable (they get blocked/moved regularly).
    private static readonly string[] Mirrors = { "archive.ph", "archive.is", "archive.md", "archive.today" };

    // Finished snapshot: https://archive.ph/AbCdE   (or /wip/AbCdE while still being captured)
    private static readonly Regex Snapshot = new(
        @"^https?://archive\.(ph|is|md|today|li|fo|vn)/(wip/)?[A-Za-z0-9]{4,10}/?$", RegexOptions.Compiled);

    // "1 month", "5 minutes", "an hour", "a few seconds" ...
    private static readonly Regex AgePattern = new(
        @"\b(?<n>\d+|an?|one|few)\s+(?<u>second|minute|hour|day|week|month|year)s?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                var captchaFailure = await HandleCaptchaAsync(page, options, staleUrl: null);
                if (captchaFailure is not null) return captchaFailure;

                await page.WaitForURLAsync(Snapshot, new PageWaitForURLOptions { Timeout = timeout });

                // ---- "This page was last archived X [measurement of time] ago" popup ----------------------------------
                // Only possible on a finished snapshot, not on a /wip/ (capture in progress) page.
                if (!page.Url.Contains("/wip/"))
                {
                    var popup = await FindAlreadyArchivedPopupAsync(page);
                    if (popup is not null)
                    {
                        var existingUrl = await GetExistingSnapshotUrlAsync(page);
                        var ageText = await SafeInnerTextAsync(popup.Locator("h1").First);
                        var known = TryParseAge(ageText, out var age);
                        var ageLabel = known ? FormatAge(ageText) : "an unknown time";

                        if (known && age < MaxReuseAge)
                        {
                            Console.WriteLine($"[{Name}] Existing snapshot is {ageLabel} old (< 1 hour); reusing it.");
                            return new ArchiveResult(Name, true, existingUrl,
                                $"Already archived {ageLabel} ago; reused the existing snapshot.");
                        }

                        Console.WriteLine(known
                            ? $"[{Name}] Existing snapshot is {ageLabel} old (>= 1 hour); saving a new one."
                            : $"[{Name}] Couldn't read the snapshot age ('{ageText.Trim()}'); saving a new one.");

                        var save = await PageHelpers.FirstVisibleAsync(page,
                            "#DIVALREADY input[type='submit']",
                            "#DIVALREADY form input[value='save']",
                            "#DIVALREADY button");
                        if (save is null)
                        {
                            await PageHelpers.ScreenshotAsync(page, options, Key);
                            return new ArchiveResult(Name, true, existingUrl,
                                $"Existing snapshot is {ageLabel} old but the 'save' button wasn't found; returning the existing link.");
                        }

                        var staleUrl = page.Url;
                        await save.ClickAsync();

                        captchaFailure = await HandleCaptchaAsync(page, options, staleUrl);
                        if (captchaFailure is not null) return captchaFailure with { ArchivedUrl = existingUrl,
                            Message = captchaFailure.Message + " (The older existing snapshot is returned as ArchivedUrl.)" };

                        // Wait for the *new* snapshot (or its /wip/ page), not the one we are already on.
                        await page.WaitForURLAsync(
                            u => Snapshot.IsMatch(u) && !SameUrl(u, staleUrl),
                            new PageWaitForURLOptions { Timeout = timeout });
                    }
                }

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

    // ------------------------------------------------------------------------------------------
    // "Already archived" popup helpers
    // ------------------------------------------------------------------------------------------

    // Returns the popup locator (#DIVALREADY) if it is visible, else null.</summary>
    private static async Task<ILocator?> FindAlreadyArchivedPopupAsync(IPage page)
    {
        var popup = page.Locator("#DIVALREADY");
        try
        {
            await popup.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3000 });
            return popup;
        }
        catch (Exception ex) when (PageHelpers.IsTimeout(ex))
        {
            return null;
        }
    }

    // The popup's close (X) link points at the existing snapshot; fall back to the current URL.
    private static async Task<string> GetExistingSnapshotUrlAsync(IPage page)
    {
        try
        {
            var href = await page.Locator("#DIVALREADY a:has(#ALREADY_CLOSEBTN)").First
                .GetAttributeAsync("href", new LocatorGetAttributeOptions { Timeout = 2000 });
            if (!string.IsNullOrWhiteSpace(href) && Snapshot.IsMatch(href)) return href;
        }
        catch (PlaywrightException) { }
        return page.Url;
    }

    private static async Task<string> SafeInnerTextAsync(ILocator locator)
    {
        try { return await locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }); }
        catch (PlaywrightException) { return string.Empty; }
    }

    /* Summary
     Parses text like "This page was last archived 5 minutes ago" / "1 month" / "an hour".
     Months are treated as 30 days and years as 365 days.
    */ 
    internal static bool TryParseAge(string text, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        var m = AgePattern.Match(text);
        if (!m.Success) return false;

        var n = m.Groups["n"].Value.ToLowerInvariant() switch
        {
            "a" or "an" or "one" => 1,
            "few" => 3,
            var digits => int.Parse(digits),
        };

        age = m.Groups["u"].Value.ToLowerInvariant() switch
        {
            "second" => TimeSpan.FromSeconds(n),
            "minute" => TimeSpan.FromMinutes(n), // I don't think Archive.today allows for resaving pages within 1 hour but better to have it.
            "hour" => TimeSpan.FromHours(n),
            "day" => TimeSpan.FromDays(n),
            "week" => TimeSpan.FromDays(7 * n),
            "month" => TimeSpan.FromDays(30 * n),
            "year" => TimeSpan.FromDays(365 * n),
            _ => TimeSpan.Zero,
        };
        return true;
    }

     // Extracts just "1 month" from the heading for log messages.
    private static string FormatAge(string text)
    {
        var m = AgePattern.Match(text);
        return m.Success ? m.Value : text.Trim();
    }

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------
    // Captcha / form helpers
    // ------------------------------------------------------------------------------------------

     /* Summary:
     After a submit, wait briefly to see whether a captcha appears instead of progress.
     Returns a failure result (headless + captcha), or null to carry on (no captcha, or the
     user is solving it in the headed browser).
     */
    private static async Task<ArchiveResult?> HandleCaptchaAsync(IPage page, ArchiveOptions options, string? staleUrl)
    {
        if (!await WaitForCaptchaOrResultAsync(page, staleUrl)) return null;

        if (!options.Headed)
        {
            await PageHelpers.ScreenshotAsync(page, options, "archivetoday");
            return new ArchiveResult("archive.today", false, null,
                "Captcha required. Re-run with --headed to solve it manually.");
        }

        Console.WriteLine("[archive.today] Captcha shown. Solve it in the browser window...");
        return null;
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
     <paramref name="staleUrl"/> is a snapshot URL we're already on (the old snapshot) and must
     not be mistaken for "finished".
     */
    private static async Task<bool> WaitForCaptchaOrResultAsync(IPage page, string? staleUrl)
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(1000);
            if (Snapshot.IsMatch(page.Url) && (staleUrl is null || !SameUrl(page.Url, staleUrl))) return false;
            if (await PageHelpers.LooksLikeCaptchaAsync(page)) return true;
        }
        return false;
    }
}