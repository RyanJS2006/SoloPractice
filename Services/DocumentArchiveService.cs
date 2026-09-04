using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using SoloPractice.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace SoloPractice.Services;

internal enum ArchivedDocumentType
{
    Receipt = 1,
    AdditionalReceipt = 2,
    TaxForm = 3,
    InsuranceStatement = 4
}

internal sealed record DocumentArchiveCandidate(
    string SourcePath,
    string OriginalFileName,
    string Extension,
    byte[] Sha256,
    string? ExistingArchivedPath);

internal sealed record DocumentArchiveResult(
    bool AlreadyArchived,
    string ArchivedPath,
    string OriginalFileName,
    ArchivedDocumentType DocumentType,
    int Year,
    int? Month,
    string DisplayName);

internal static class DocumentArchiveService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff"
        };

    public static DocumentArchiveCandidate Prepare(string sourcePath)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The document file does not exist.", fullPath);

        string extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                $"Unsupported document type '{extension}'. Supported types are PDF, PNG, JPG, JPEG, TIF, and TIFF.");
        }

        byte[] hash = ComputeSha256(fullPath);
        string? existingRelativePath = FindExistingRelativePath(hash);
        string? existingPath = existingRelativePath is null
            ? null
            : GetAbsoluteArchivePath(existingRelativePath);
        if (existingPath is not null && !File.Exists(existingPath))
        {
            throw new InvalidDataException(
                $"SoloPractice already recorded this document, but its archived file is missing: {existingPath}");
        }

        return new DocumentArchiveCandidate(
            fullPath,
            Path.GetFileName(fullPath),
            extension,
            hash,
            existingPath);
    }

    public static DocumentArchiveResult Archive(
        DocumentArchiveCandidate candidate,
        ArchivedDocumentType documentType,
        int year,
        int? month,
        string displayName)
    {
        ValidateMetadata(documentType, year, month);

        string? existingRelativePath = FindExistingRelativePath(candidate.Sha256);
        if (existingRelativePath is not null)
        {
            return new DocumentArchiveResult(
                true,
                GetAbsoluteArchivePath(existingRelativePath),
                candidate.OriginalFileName,
                documentType,
                year,
                month,
                displayName);
        }

        AppPaths.EnsureAccountingYearDirectoriesExist(year);
        string destinationDirectory = GetDestinationDirectory(documentType, year, month);
        Directory.CreateDirectory(destinationDirectory);

        string safeName = SanitizeDisplayName(displayName);
        string prefix = documentType == ArchivedDocumentType.Receipt
            ? $"{year}-{month:00}_"
            : $"{year}_";
        string finalPath = FindAvailablePath(
            destinationDirectory,
            prefix + safeName,
            candidate.Extension);
        string temporaryPath = Path.Combine(
            destinationDirectory,
            "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        bool finalFileCreated = false;
        try
        {
            File.Copy(candidate.SourcePath, temporaryPath, overwrite: false);
            byte[] copiedHash = ComputeSha256(temporaryPath);
            if (!CryptographicOperations.FixedTimeEquals(candidate.Sha256, copiedHash))
                throw new IOException("The archived copy did not match the source document.");

            File.Move(temporaryPath, finalPath);
            finalFileCreated = true;

            InsertMetadata(
                candidate,
                documentType,
                year,
                month,
                safeName,
                finalPath);

            return new DocumentArchiveResult(
                false,
                finalPath,
                candidate.OriginalFileName,
                documentType,
                year,
                month,
                safeName);
        }
        catch
        {
            TryDelete(temporaryPath);
            if (finalFileCreated)
                TryDelete(finalPath);
            throw;
        }
    }

    public static string GetTypeLabel(ArchivedDocumentType documentType) =>
        documentType switch
        {
            ArchivedDocumentType.Receipt => "Receipt",
            ArchivedDocumentType.AdditionalReceipt => "Additional Receipt",
            ArchivedDocumentType.TaxForm => "Tax Form",
            ArchivedDocumentType.InsuranceStatement => "Insurance Statement",
            _ => throw new ArgumentOutOfRangeException(nameof(documentType))
        };

    private static void InsertMetadata(
        DocumentArchiveCandidate candidate,
        ArchivedDocumentType documentType,
        int year,
        int? month,
        string displayName,
        string finalPath)
    {
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using (SqliteCommand timestamp = connection.CreateCommand())
        {
            timestamp.Transaction = transaction;
            timestamp.CommandText = """
                INSERT INTO TimestampValues (UnixSeconds)
                VALUES ($unixSeconds)
                ON CONFLICT (UnixSeconds) DO NOTHING;
                """;
            timestamp.Parameters.AddWithValue("$unixSeconds", unixSeconds);
            timestamp.ExecuteNonQuery();
        }

        string relativePath = Path.GetRelativePath(
                AppPaths.ApplicationDirectory,
                finalPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ArchivedDocuments
            (
                Sha256,
                DocumentTypeId,
                Year,
                Month,
                OriginalFileName,
                ArchivedFileName,
                ArchivedRelativePath,
                DisplayName,
                ArchivedTimestampId
            )
            VALUES
            (
                $sha256,
                $documentType,
                $year,
                $month,
                $originalFileName,
                $archivedFileName,
                $relativePath,
                $displayName,
                (SELECT Id FROM TimestampValues WHERE UnixSeconds = $unixSeconds)
            );
            """;
        insert.Parameters.Add("$sha256", SqliteType.Blob).Value = candidate.Sha256;
        insert.Parameters.AddWithValue("$documentType", (int)documentType);
        insert.Parameters.AddWithValue("$year", year);
        insert.Parameters.AddWithValue("$month", month.HasValue ? month.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$originalFileName", candidate.OriginalFileName);
        insert.Parameters.AddWithValue("$archivedFileName", Path.GetFileName(finalPath));
        insert.Parameters.AddWithValue("$relativePath", relativePath);
        insert.Parameters.AddWithValue("$displayName", displayName);
        insert.Parameters.AddWithValue("$unixSeconds", unixSeconds);
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string? FindExistingRelativePath(byte[] sha256)
    {
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ArchivedRelativePath
            FROM ArchivedDocuments
            WHERE Sha256 = $sha256;
            """;
        command.Parameters.Add("$sha256", SqliteType.Blob).Value = sha256;
        return command.ExecuteScalar() as string;
    }

    private static string GetDestinationDirectory(
        ArchivedDocumentType documentType,
        int year,
        int? month) =>
        documentType switch
        {
            ArchivedDocumentType.Receipt =>
                AppPaths.GetMonthlyReceiptsDirectory(year, month!.Value),
            ArchivedDocumentType.AdditionalReceipt =>
                AppPaths.GetAdditionalReceiptsDirectory(year),
            ArchivedDocumentType.TaxForm =>
                AppPaths.GetTaxFormsDirectory(year),
            ArchivedDocumentType.InsuranceStatement =>
                AppPaths.GetInsuranceStatementsDirectory(year),
            _ => throw new ArgumentOutOfRangeException(nameof(documentType))
        };

    private static string FindAvailablePath(
        string directory,
        string baseName,
        string extension)
    {
        string candidate = Path.Combine(directory, baseName + extension);
        for (int suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{baseName}_{suffix}{extension}");
        return candidate;
    }

    private static string SanitizeDisplayName(string displayName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new StringBuilder(displayName.Length);
        bool previousUnderscore = false;

        foreach (char character in displayName.Trim())
        {
            char value = invalid.Contains(character) || char.IsWhiteSpace(character)
                ? '_'
                : character;
            if (value == '_' && previousUnderscore)
                continue;
            result.Append(value);
            previousUnderscore = value == '_';
        }

        string safe = result.ToString().Trim(' ', '.', '_');
        return safe.Length == 0 ? "Document_001" : safe;
    }

    private static byte[] ComputeSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static string GetAbsoluteArchivePath(string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(AppPaths.ApplicationDirectory, normalized));
        string root = Path.GetFullPath(AppPaths.ApplicationDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An archived document path points outside SoloPractice.");
        return fullPath;
    }

    private static void ValidateMetadata(
        ArchivedDocumentType documentType,
        int year,
        int? month)
    {
        if (year is < 2000 or > 9998)
            throw new ArgumentOutOfRangeException(nameof(year));
        if (documentType == ArchivedDocumentType.Receipt && month is not (>= 1 and <= 12))
            throw new ArgumentOutOfRangeException(nameof(month));
        if (documentType != ArchivedDocumentType.Receipt && month is not null)
            throw new ArgumentException("Only a monthly receipt may have a month.", nameof(month));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
