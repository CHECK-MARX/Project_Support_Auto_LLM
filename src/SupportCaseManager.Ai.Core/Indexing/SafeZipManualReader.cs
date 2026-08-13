using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record SafeZipManualReaderOptions
{
    public const int DefaultMaxEntries = 1000;
    public const long DefaultMaxEntryUncompressedBytes = 32L * 1024 * 1024;
    public const long DefaultMaxTotalUncompressedBytes = 256L * 1024 * 1024;
    public const double DefaultMaxCompressionRatio = 200d;

    public int MaxEntries { get; init; } = DefaultMaxEntries;
    public long MaxEntryUncompressedBytes { get; init; } = DefaultMaxEntryUncompressedBytes;
    public long MaxTotalUncompressedBytes { get; init; } = DefaultMaxTotalUncompressedBytes;
    public double MaxCompressionRatio { get; init; } = DefaultMaxCompressionRatio;
    public string? TemporaryRootPath { get; init; }
}

public sealed record SafeZipManualEntry
{
    public string ArchivePath { get; init; } = string.Empty;
    public string EntryPath { get; init; } = string.Empty;
    public string OriginalFileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public long UncompressedSize { get; init; }
    public long CompressedSize { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public DateTimeOffset? LastModifiedAt { get; init; }
    internal ManualDocumentContent Content { get; init; } = new(string.Empty, string.Empty);
}

public sealed record SafeZipManualReadResult
{
    public int ZipEntryCount { get; init; }
    public int SupportedEntryCount { get; init; }
    public int SkippedEntryCount { get; init; }
    public int UnsafePathRejectedCount { get; init; }
    public int SizeLimitExceededCount { get; init; }
    public int EncryptedArchiveCount { get; init; }
    public int CorruptArchiveCount { get; init; }
    public IReadOnlyList<SafeZipManualEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class SafeZipManualReader
{
    private static readonly Regex DrivePathRegex = new(@"^[A-Za-z]:", RegexOptions.Compiled);
    private readonly SafeZipManualReaderOptions options;

    static SafeZipManualReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SafeZipManualReader(SafeZipManualReaderOptions? options = null)
    {
        this.options = options ?? new SafeZipManualReaderOptions();
        ValidateOptions(this.options);
    }

    public async Task<SafeZipManualReadResult> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var entries = new List<SafeZipManualEntry>();
        var zipEntryCount = 0;
        var supportedEntryCount = 0;
        var skippedEntryCount = 0;
        var unsafePathRejectedCount = 0;
        var sizeLimitExceededCount = 0;
        var encryptedArchiveCount = 0;
        var corruptArchiveCount = 0;
        string? temporaryDirectory = null;

        try
        {
            if (HasEncryptedEntries(archivePath))
            {
                warnings.Add($"Encrypted ZIP was skipped: {archivePath}");
                return new SafeZipManualReadResult
                {
                    EncryptedArchiveCount = 1,
                    Warnings = warnings,
                };
            }

            using var archive = ZipFile.OpenRead(archivePath);
            zipEntryCount = archive.Entries.Count(static entry => !IsDirectory(entry));
            if (zipEntryCount > options.MaxEntries)
            {
                warnings.Add($"ZIP entry limit exceeded: {archivePath}. entries={zipEntryCount}; limit={options.MaxEntries}");
                return new SafeZipManualReadResult
                {
                    ZipEntryCount = zipEntryCount,
                    SkippedEntryCount = zipEntryCount,
                    SizeLimitExceededCount = 1,
                    Warnings = warnings,
                };
            }

            var declaredTotal = 0L;
            foreach (var entry in archive.Entries.Where(static entry => !IsDirectory(entry)))
            {
                if (entry.Length > options.MaxTotalUncompressedBytes - declaredTotal)
                {
                    declaredTotal = options.MaxTotalUncompressedBytes + 1;
                    break;
                }

                declaredTotal += entry.Length;
            }
            if (declaredTotal > options.MaxTotalUncompressedBytes)
            {
                warnings.Add($"ZIP total uncompressed size limit exceeded: {archivePath}. bytes={declaredTotal}; limit={options.MaxTotalUncompressedBytes}");
                return new SafeZipManualReadResult
                {
                    ZipEntryCount = zipEntryCount,
                    SkippedEntryCount = zipEntryCount,
                    SizeLimitExceededCount = 1,
                    Warnings = warnings,
                };
            }

            temporaryDirectory = CreateTemporaryDirectory();
            var extractedTotal = 0L;
            var entryNumber = 0;
            foreach (var entry in archive.Entries.OrderBy(static item => item.FullName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDirectory(entry))
                {
                    continue;
                }

                entryNumber += 1;
                var normalizedEntryPath = entry.FullName.Replace('\\', '/');
                if (!IsSafeEntryPath(normalizedEntryPath))
                {
                    unsafePathRejectedCount += 1;
                    skippedEntryCount += 1;
                    warnings.Add($"Unsafe ZIP entry path rejected: {archivePath}!/{normalizedEntryPath}");
                    continue;
                }

                var classification = ManualDocumentFilter.ClassifyFile(normalizedEntryPath);
                if (classification.Category != ManualDocumentCategory.ImportCandidate)
                {
                    skippedEntryCount += 1;
                    continue;
                }

                supportedEntryCount += 1;
                if (entry.Length > options.MaxEntryUncompressedBytes
                    || extractedTotal + entry.Length > options.MaxTotalUncompressedBytes
                    || CompressionRatio(entry) > options.MaxCompressionRatio)
                {
                    sizeLimitExceededCount += 1;
                    skippedEntryCount += 1;
                    warnings.Add($"ZIP entry size or compression ratio limit exceeded: {archivePath}!/{normalizedEntryPath}");
                    continue;
                }

                var extension = ManualDocumentFilter.NormalizeExtension(Path.GetExtension(normalizedEntryPath));
                var temporaryPath = Path.Combine(temporaryDirectory, $"entry-{entryNumber:D6}{(extension == "(none)" ? string.Empty : extension)}");
                try
                {
                    await using (var source = entry.Open())
                    await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await CopyWithLimitAsync(source, destination, options.MaxEntryUncompressedBytes, cancellationToken);
                    }

                    var copiedLength = new FileInfo(temporaryPath).Length;
                    extractedTotal += copiedLength;
                    if (extractedTotal > options.MaxTotalUncompressedBytes)
                    {
                        sizeLimitExceededCount += 1;
                        skippedEntryCount += 1;
                        warnings.Add($"ZIP extracted size limit exceeded: {archivePath}!/{normalizedEntryPath}");
                        continue;
                    }

                    var content = await ManualDocumentTextExtractor.ReadAsync(temporaryPath, normalizedEntryPath, cancellationToken);
                    var sha256 = await ComputeSha256Async(temporaryPath, cancellationToken);
                    entries.Add(new SafeZipManualEntry
                    {
                        ArchivePath = Path.GetFullPath(archivePath),
                        EntryPath = normalizedEntryPath,
                        OriginalFileName = Path.GetFileName(normalizedEntryPath),
                        Extension = extension,
                        UncompressedSize = entry.Length,
                        CompressedSize = entry.CompressedLength,
                        Sha256 = sha256,
                        LastModifiedAt = entry.LastWriteTime == default ? null : entry.LastWriteTime,
                        Content = content,
                    });
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    skippedEntryCount += 1;
                    warnings.Add($"ZIP entry could not be read and was skipped: {archivePath}!/{normalizedEntryPath}. {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    TryDeleteFile(temporaryPath);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            corruptArchiveCount = 1;
            warnings.Add($"Corrupt ZIP was skipped: {archivePath}. {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            warnings.Add($"ZIP could not be read and was skipped: {archivePath}. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        return new SafeZipManualReadResult
        {
            ZipEntryCount = zipEntryCount,
            SupportedEntryCount = supportedEntryCount,
            SkippedEntryCount = skippedEntryCount,
            UnsafePathRejectedCount = unsafePathRejectedCount,
            SizeLimitExceededCount = sizeLimitExceededCount,
            EncryptedArchiveCount = encryptedArchiveCount,
            CorruptArchiveCount = corruptArchiveCount,
            Entries = entries,
            Warnings = warnings,
        };
    }

    public static bool IsSafeEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        var normalized = entryPath.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || DrivePathRegex.IsMatch(normalized)
            || Path.IsPathRooted(normalized))
        {
            return false;
        }

        return !normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is ".." or ".");
    }

    private string CreateTemporaryDirectory()
    {
        var root = string.IsNullOrWhiteSpace(options.TemporaryRootPath)
            ? Path.GetTempPath()
            : options.TemporaryRootPath;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"support-case-manager-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name);

    private static double CompressionRatio(ZipArchiveEntry entry)
    {
        if (entry.Length == 0)
        {
            return 1d;
        }

        return entry.CompressedLength <= 0 ? double.PositiveInfinity : (double)entry.Length / entry.CompressedLength;
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var total = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"ZIP entry exceeded the maximum uncompressed size of {maxBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasEncryptedEntries(string archivePath)
    {
        try
        {
            using var stream = File.OpenRead(archivePath);
            if (stream.Length < 22)
            {
                return false;
            }

            var tailLength = (int)Math.Min(stream.Length, ushort.MaxValue + 22L);
            var tail = new byte[tailLength];
            stream.Seek(-tailLength, SeekOrigin.End);
            stream.ReadExactly(tail);
            var eocdOffset = FindSignatureBackward(tail, 0x06054b50);
            if (eocdOffset < 0 || eocdOffset + 22 > tail.Length)
            {
                return false;
            }

            var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocdOffset + 10, 2));
            var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocdOffset + 16, 4));
            stream.Seek(centralOffset, SeekOrigin.Begin);
            var header = new byte[46];
            for (var index = 0; index < entryCount; index++)
            {
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != 0x02014b50)
                {
                    return false;
                }

                var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
                if ((flags & 0x0001) != 0)
                {
                    return true;
                }

                var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
                stream.Seek(fileNameLength + extraLength + commentLength, SeekOrigin.Current);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentOutOfRangeException)
        {
        }

        return false;
    }

    private static int FindSignatureBackward(byte[] bytes, uint signature)
    {
        for (var index = bytes.Length - 4; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == signature)
            {
                return index;
            }
        }

        return -1;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateOptions(SafeZipManualReaderOptions value)
    {
        if (value.MaxEntries <= 0 || value.MaxEntryUncompressedBytes <= 0 || value.MaxTotalUncompressedBytes <= 0 || value.MaxCompressionRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "ZIP safety limits must be positive.");
        }
    }
}
