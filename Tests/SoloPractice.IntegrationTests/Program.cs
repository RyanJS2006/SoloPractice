using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using SoloPractice.Services;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

if (args.Length < 2)
    throw new ArgumentException("Usage: integration-test <database> <Chase CSV> [<Chase CSV> ...]");

string databasePath = Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("SOLOPRACTICE_DATABASE_PATH", databasePath);
if (File.Exists(databasePath))
    File.Delete(databasePath);

Database.Initialize();
var timer = Stopwatch.StartNew();
var firstResults = new List<ChaseImportResult>();
foreach (string csv in args.Skip(1))
    firstResults.Add(ChaseCsvImporter.Import(csv));
timer.Stop();

long firstImportMilliseconds = timer.ElapsedMilliseconds;
timer.Restart();
foreach (string csv in args.Skip(1))
{
    ChaseImportResult duplicate = ChaseCsvImporter.Import(csv);
    if (!duplicate.FileAlreadyImported)
        throw new InvalidOperationException($"Exact re-import was not rejected: {csv}");
}
timer.Stop();

using SqliteConnection connection = Database.OpenConnection();
static long Scalar(SqliteConnection c, string sql)
{
    using SqliteCommand command = c.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

using (SqliteCommand integrity = connection.CreateCommand())
{
    integrity.CommandText = "PRAGMA integrity_check;";
    if (!string.Equals((string?)integrity.ExecuteScalar(), "ok", StringComparison.Ordinal))
        throw new InvalidOperationException("integrity_check failed");
}
if (Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;") != 0)
    throw new InvalidOperationException("foreign_key_check failed");
if (Scalar(connection, "SELECT count(*) FROM ImportFiles;") != args.Length - 1)
    throw new InvalidOperationException("Import-file provenance count mismatch");
if (Scalar(connection, "SELECT count(*) FROM ImportRows;") != firstResults.Sum(x => x.RowsRead))
    throw new InvalidOperationException("Import-row provenance count mismatch");
if (Scalar(connection, "SELECT count(*) FROM UnparsedDepositDescriptions;") != firstResults.Sum(x => x.UnparsedDescriptions))
    throw new InvalidOperationException("Unparsed-description count mismatch");

using (SqliteCommand sources = connection.CreateCommand())
{
    sources.CommandText = "SELECT f.FileSha256,s.GzipData FROM ImportFiles f JOIN ImportSourceData s ON s.ImportFileId=f.Id;";
    using SqliteDataReader sourceRows = sources.ExecuteReader();
    while (sourceRows.Read())
    {
        byte[] expectedHash = (byte[])sourceRows[0];
        using var compressed = new MemoryStream((byte[])sourceRows[1], writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var exactSource = new MemoryStream();
        gzip.CopyTo(exactSource);
        if (!SHA256.HashData(exactSource.ToArray()).AsSpan().SequenceEqual(expectedHash))
            throw new InvalidOperationException("Compressed source does not round-trip to its recorded SHA-256");
    }
}

if (args.Length >= 5 && (firstResults[^1].NewTransactions != 0 || firstResults[^1].ReusedTransactions == 0))
    throw new InvalidOperationException("Overlapping-file fixture did not reuse canonical transactions");

using (SqliteCommand optimize = connection.CreateCommand())
{
    optimize.CommandText = "PRAGMA optimize; VACUUM;";
    optimize.ExecuteNonQuery();
}

Console.WriteLine($"transactions={Scalar(connection, "SELECT count(*) FROM Transactions;")}");
Console.WriteLine($"depositTransactions={Scalar(connection, "SELECT count(*) FROM DepositTransactions;")}");
Console.WriteLine($"creditCardTransactions={Scalar(connection, "SELECT count(*) FROM CreditCardTransactions;")}");
Console.WriteLine($"importFiles={Scalar(connection, "SELECT count(*) FROM ImportFiles;")}");
Console.WriteLine($"importRows={Scalar(connection, "SELECT count(*) FROM ImportRows;")}");
Console.WriteLine($"unparsed={Scalar(connection, "SELECT count(*) FROM UnparsedDepositDescriptions;")}");
Console.WriteLine($"firstImportMilliseconds={firstImportMilliseconds}");
Console.WriteLine($"exactReimportMilliseconds={timer.ElapsedMilliseconds}");
Console.WriteLine($"bytes={new FileInfo(databasePath).Length}");
Console.WriteLine($"pages={Scalar(connection, "PRAGMA page_count;")}");
