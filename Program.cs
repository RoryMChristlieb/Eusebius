using ArchiveSaver.Archivers;
using Microsoft.Playwright;

namespace ArchiveSaver;

internal static class Program
{
    private static readonly IArchiver[] AllArchivers =
    {
        new WaybackArchiver(),
        new ArchiveTodayArchiver(),
        new GhostArchiveArchiver(),
        new ArquivoArchiver(),
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        // ---- Parse arguments ----
        string? rawUrl = null;
        var headed = false;
        var parallel = false;
        var timeoutSeconds = 120;
        string? screenshotDir = null;
        HashSet<string>? only = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headed": headed = true; break;
                case "--parallel": parallel = true; break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[++i], out var t) && t > 0:
                    timeoutSeconds = t; break;
                case "--screenshots" when i + 1 < args.Length:
                    screenshotDir = args[++i]; break;
                case "--sites" when i + 1 < args.Length:
                    only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Select(s => s.ToLowerInvariant()).ToHashSet();
                    break;
                default:
                    if (args[i].StartsWith("--")) { Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}"); PrintUsage(); return 1; }
                    rawUrl ??= args[i];
                    break;
            }
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine("Please provide a valid http(s) URL.");
            return 1;
        }
        var url = uri.AbsoluteUri;

        var archivers = AllArchivers.Where(a => only is null || only.Contains(a.Key)).ToList();
        if (archivers.Count == 0)
        {
            Console.Error.WriteLine($"No matching sites. Valid keys: {string.Join(", ", AllArchivers.Select(a => a.Key))}");
            return 1;
        }

        // archive.today usually needs a human for its captcha, which cannot be solved headless.
        if (!headed && archivers.Any(a => a.Key == "archivetoday"))
            Console.WriteLine("Note: archive.today often requires a captcha. Use --headed to solve it manually.\n");

        var options = new ArchiveOptions(headed, timeoutSeconds, screenshotDir);
        Console.WriteLine($"Archiving: {url}\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // ---- Run ----
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !headed,
        });

        async Task<ArchiveResult> RunOne(IArchiver archiver)
        {
            // Separate context per site: isolated cookies/storage, and safe to run in parallel.
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                Locale = "en-US",
            });
            var page = await context.NewPageAsync();
            Console.WriteLine($"[{archiver.Name}] submitting...");
            try
            {
                var result = await archiver.SaveAsync(page, url, options, cts.Token);
                Console.WriteLine($"[{archiver.Name}] {(result.Success ? "OK" : "FAILED")}");
                return result;
            }
            catch (Exception ex)
            {
                return new ArchiveResult(archiver.Name, false, null, ex.Message);
            }
        }

        var results = new List<ArchiveResult>();
        if (parallel)
        {
            results.AddRange(await Task.WhenAll(archivers.Select(RunOne)));
        }
        else
        {
            foreach (var a in archivers)
            {
                if (cts.IsCancellationRequested) break;
                results.Add(await RunOne(a));
            }
        }

        // ---- Summary ----
        Console.WriteLine("\n==== Summary ====");
        foreach (var r in results)
        {
            Console.WriteLine($"{(r.Success ? "[OK]    " : "[FAILED]")} {r.Site}");
            if (r.ArchivedUrl is not null) Console.WriteLine($"         {r.ArchivedUrl}");
            if (!r.Success || r.Message != "Saved.") Console.WriteLine($"         {r.Message}");
        }

        return results.All(r => r.Success) ? 0 : 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ArchiveSaver - submit a URL to several web archives with Playwright.

            Usage:
              dotnet run -- <url> [options]

            Options:
              --sites <list>        Comma-separated: wayback,archivetoday,ghostarchive,arquivo (default: all)
              --headed              Show the browser window (needed to solve archive.today captchas)
              --parallel            Submit to all sites at the same time (default: one after another)
              --timeout <seconds>   Per-site wait for the archive to finish (default: 120)
              --screenshots <dir>   Save a screenshot when a site fails or times out

            Exit codes: 0 all saved, 1 bad input, 2 at least one site failed.
            """);
    }
}
