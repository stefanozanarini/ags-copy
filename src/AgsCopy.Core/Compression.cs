namespace AgsCopy.Core;

/// <summary>Which files are worth compressing: everything but formats that already are.</summary>
public static class Compression
{
    /// <summary>Suffix of files the copy engine compresses itself (gzip, opened by 7-Zip or Windows 11 Explorer).</summary>
    public const string Suffix = ".gz";

    private static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        // Archives and compressed streams
        ".7z", ".zip", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz", ".lz4", ".lzma", ".cab", ".arj", ".z", ".zipx",
        ".jar", ".war", ".apk", ".msi", ".msix", ".appx", ".nupkg", ".whl", ".deb", ".rpm", ".dmg", ".wim", ".esd",
        // Images
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl", ".jp2",
        // Audio and video
        ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".flac", ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm",
        ".flv", ".mpg", ".mpeg", ".3gp", ".ts", ".mts", ".m2ts",
        // Documents that are zip files, and PDFs (compressed inside)
        ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".epub", ".pdf", ".xps", ".oxps",
        // Encrypted or already packed data
        ".vhdx", ".pst.gz", ".gpg", ".pgp", ".aes", ".kdbx", ".tib", ".vbk", ".vib",
    };

    public static bool IsCompressible(string fileName) => !Compressed.Contains(Path.GetExtension(fileName));
}
