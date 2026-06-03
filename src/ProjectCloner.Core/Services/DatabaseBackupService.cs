using System.Text;
using System.Text.RegularExpressions;
using ProjectCloner.Core.Config;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

public interface IDatabaseBackupService
{
    /// <summary>
    /// Best-effort MySQL backup. Reads the DB host/IP from the source's bitbucket-pipelines.yml,
    /// then dumps the database while excluding the <i>data</i> of the configured tables
    /// (their schema is kept, rows are not). Never throws — returns false and logs a warning on any problem.
    /// </summary>
    Task<bool> TryBackupAsync(string sourceProjectPath, DatabaseSettings settings,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default);
}

/// <summary>Dumps a MySQL database via <c>mysqldump</c>, excluding the data of log/history tables.</summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private static readonly Regex IpV4 = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b",
        RegexOptions.Compiled);

    private readonly ProcessRunner _runner;

    public DatabaseBackupService(ProcessRunner runner) => _runner = runner;

    public async Task<bool> TryBackupAsync(string sourceProjectPath, DatabaseSettings settings,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.DatabaseName))
            {
                log.Warning("DB backup skipped: username/database not configured.");
                return false;
            }

            var pipelineFile = PipelineCleaner.FindPipelineFile(sourceProjectPath);
            if (pipelineFile is null)
            {
                log.Warning($"DB backup skipped: no {PipelineCleaner.PipelineFileName} in the source to read the host from.");
                return false;
            }

            var host = ExtractHost(File.ReadAllText(pipelineFile));
            if (host is null)
            {
                log.Warning("DB backup skipped: no IP address found in bitbucket-pipelines.yml.");
                return false;
            }
            log.Info($"DB host resolved from pipeline file: {host}");

            var backupFolder = string.IsNullOrWhiteSpace(settings.BackupFolder)
                ? Path.Combine(Path.GetTempPath(), "projectcloner-backups")
                : settings.BackupFolder;
            Directory.CreateDirectory(backupFolder);
            var outFile = Path.Combine(backupFolder, $"{settings.DatabaseName}_{host}.sql");

            var env = new Dictionary<string, string> { ["MYSQL_PWD"] = settings.Password };
            var port = settings.Port > 0 ? settings.Port : 3306;
            var tables = settings.TablesToClear.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();

            // Pass 1: full dump, omitting the heavy tables entirely.
            var dumpArgs = new List<string>
            {
                "-h", host, "-P", port.ToString(), "-u", settings.Username,
                "--single-transaction", "--routines", "--triggers"
            };
            foreach (var table in tables)
                dumpArgs.Add($"--ignore-table={settings.DatabaseName}.{table}");
            dumpArgs.Add(settings.DatabaseName);

            log.Step($"mysqldump → {outFile}");
            var main = await _runner.RunAsync("mysqldump", dumpArgs, backupFolder, env,
                onOutput: null, cancellationToken: ct);
            if (!main.Success)
            {
                log.Warning($"DB backup failed (mysqldump): {Truncate(main.StdErr)}");
                return false;
            }

            var sql = new StringBuilder(main.StdOut);

            // Pass 2: append schema-only definitions for the excluded tables (structure kept, no rows).
            if (tables.Count > 0)
            {
                var schemaArgs = new List<string>
                {
                    "-h", host, "-P", port.ToString(), "-u", settings.Username, "--no-data", settings.DatabaseName
                };
                schemaArgs.AddRange(tables);

                var schema = await _runner.RunAsync("mysqldump", schemaArgs, backupFolder, env,
                    onOutput: null, cancellationToken: ct);
                if (schema.Success)
                {
                    sql.AppendLine();
                    sql.AppendLine("-- Schema-only definitions for excluded (cleared) tables:");
                    sql.AppendLine(schema.StdOut);
                }
                else
                {
                    log.Warning($"Could not dump schema for excluded tables: {Truncate(schema.StdErr)}");
                }
            }

            await File.WriteAllTextAsync(outFile, sql.ToString(), ct);
            log.Success($"DB backup written: {outFile} ({tables.Count} table(s) excluded from data).");
            return true;
        }
        catch (Exception ex)
        {
            // Backup must never break the clone flow.
            log.Warning($"DB backup skipped due to error: {ex.Message}");
            return false;
        }
    }

    internal static string? ExtractHost(string pipelineContent)
    {
        var match = IpV4.Match(pipelineContent);
        return match.Success ? match.Value : null;
    }

    private static string Truncate(string value, int max = 300)
        => value.Length <= max ? value : value[..max] + "…";
}
