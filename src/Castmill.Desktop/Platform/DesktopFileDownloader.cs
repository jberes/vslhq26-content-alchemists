using Castmill.UI.Design;
using Castmill.UI.Http;

namespace Castmill.Desktop.Platform;

public sealed class DesktopFileDownloader : IFileDownloader
{
    public async Task SaveAsync(DownloadedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);

        var fileName = Path.GetFileName(file.FileName);
        var destination = Path.Combine(downloads, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var copy = 2; File.Exists(destination); copy++)
        {
            destination = Path.Combine(downloads, $"{stem} ({copy}){extension}");
        }

        await File.WriteAllBytesAsync(destination, file.Bytes);
    }
}