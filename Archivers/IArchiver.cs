using Microsoft.Playwright;

namespace ArchiveSaver.Archivers;

/* Summary:
 Outcome of submitting one URL to one archive site.
*/
public record ArchiveResult(string Site, bool Success, string? ArchivedUrl, string Message);

/* Summary:
 Settings shared by every archiver.
*/

public record ArchiveOptions(bool Headed, int TimeoutSeconds, string? ScreenshotDir);

public interface IArchiver
{
    /* Summary:
     Short key used with --sites (e.g. "wayback").
    */
    string Key { get; }

    /* Summary:
     Human-readable name used in output.
    */
    string Name { get; }

    /* Summary:
     Submit <paramref name="url"/> and wait for the archive to finish (or fail =[).
    */
    Task<ArchiveResult> SaveAsync(IPage page, string url, ArchiveOptions options, CancellationToken ct);
}
