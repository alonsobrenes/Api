using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EPApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EPApi.Services.Archive
{
    /// <summary>
    /// Servicio de archivado: mueve archivos eliminados (soft) a /archive/ y
    /// pasa sus filas de dbo.patient_files a dbo.patient_files_archive, con bitácora en dbo.archive_runs.
    /// </summary>
    public sealed class ArchiveService : IArchiveService
    {
        private readonly string _cs;
        private readonly StorageOptions _storage;
        private readonly StorageArchiveOptions _archive;
        private readonly ILogger<ArchiveService> _log;

        public ArchiveService(
        IConfiguration cfg,
        IOptions<StorageOptions> storage,
        IOptions<StorageArchiveOptions> archive,
        ILogger<ArchiveService> log)
        {
            _cs = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Missing Default connection string");
            _storage = storage.Value ?? throw new ArgumentNullException(nameof(storage));
            _archive = archive.Value ?? throw new ArgumentNullException(nameof(archive));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        private sealed record Candidate(Guid FileId, Guid OrgId, Guid PatientId, string StorageKey, DateTime DeletedAtUtc);

        public async Task<(int ok, int fail)> RunOnceAsync(CancellationToken ct)
        {
            var (cands, runId) = await LoadCandidatesAsync(ct).ConfigureAwait(false);
            int ok = 0;
            int fail = 0;

            foreach (var c in cands)
            {
                try
                {
                    await ProcessCandidateAsync(c, ct).ConfigureAwait(false);
                    ok++;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Archive failed for file {FileId}", c.FileId);
                    fail++;
                }
            }

            await UpdateRunAsync(runId, ok, fail, null, ct).ConfigureAwait(false);
            return (ok, fail);
        }

        private async Task<(List<Candidate> list, Guid runId)> LoadCandidatesAsync(CancellationToken ct)
        {
            var list = new List<Candidate>();
            var runId = Guid.NewGuid();

            string sql = @"
INSERT INTO dbo.archive_runs(run_id) VALUES (@run);
DECLARE @cut DATETIME2(3) = DATEADD(DAY, -@days, SYSUTCDATETIME());
SELECT TOP (@batch)
pf.file_id, pf.org_id, pf.patient_id, pf.storage_key, pf.deleted_at_utc
FROM dbo.patient_files pf
WHERE pf.deleted_at_utc IS NOT NULL
AND pf.deleted_at_utc <= @cut
ORDER BY pf.deleted_at_utc;";

            await using (var cn = new SqlConnection(_cs))
            {
                await cn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new SqlCommand(sql, cn);
                cmd.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = runId });
                cmd.Parameters.Add(new SqlParameter("@days", SqlDbType.Int) { Value = _archive.RetentionDays });
                cmd.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = _archive.BatchSize });

                await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await rd.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(new Candidate(
                    rd.GetGuid(0),
                    rd.GetGuid(1),
                    rd.GetGuid(2),
                    rd.GetString(3),
                    rd.GetDateTime(4)
                    ));
                }
            }

            return (list, runId);
        }

        private string CombineUnderRoot(string relative)
        {
            var root = _storage.Local?.Root ?? throw new InvalidOperationException("Storage.Local.Root is not configured.");
            return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private string BuildArchiveKey(Guid org, Guid patient, Guid fileId)
        {
            // Normalizamos en formato con '/'
            return Path
            .Combine(_archive.FolderName, "org", org.ToString("N"), "patient", patient.ToString("N"), fileId.ToString("N"))
            .Replace('\\', '/');
        }

        private async Task ProcessCandidateAsync(Candidate c, CancellationToken ct)
        {
            // --- Paso FS A: mover a _archtmp ---
            string srcPath = CombineUnderRoot(c.StorageKey);
            string tmpRoot = CombineUnderRoot(_archive.TempFolderName);
            Directory.CreateDirectory(tmpRoot);
            string tmpPath = Path.Combine(tmpRoot, c.FileId.ToString("N"));

            bool hadFile = File.Exists(srcPath);
            if (hadFile)
            {
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); }
                        catch { /* swallow */ }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(tmpPath)!);
                    File.Move(srcPath, tmpPath);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not move file to temp for {FileId}. Proceeding with DB-only archive.", c.FileId);
                    hadFile = false;
                }
            }

            // --- Paso DB: insertar en archive y eliminar de tabla activa ---
            string newKey = BuildArchiveKey(c.OrgId, c.PatientId, c.FileId);

            const string SQL = @"
INSERT INTO dbo.patient_files_archive
(file_id, org_id, patient_id, original_name, content_type, byte_size,
storage_provider, storage_key, sha256_hex, comment, uploaded_by_user,
uploaded_at_utc, deleted_at_utc, archived_at_utc)
SELECT
file_id, org_id, patient_id, original_name, content_type, byte_size,
storage_provider, @newKey, sha256_hex, comment, uploaded_by_user,
uploaded_at_utc, deleted_at_utc, SYSUTCDATETIME()
FROM dbo.patient_files WITH (HOLDLOCK, UPDLOCK)
WHERE file_id = @id;

DELETE FROM dbo.patient_files WHERE file_id = @id;";

            await using (var cn = new SqlConnection(_cs))
            {
                await cn.OpenAsync(ct).ConfigureAwait(false);
                await using var tx = await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);

                try
                {
                    await using (var cmd = new SqlCommand(SQL, cn, (SqlTransaction)tx))
                    {
                        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = c.FileId });
                        cmd.Parameters.Add(new SqlParameter("@newKey", SqlDbType.NVarChar, 400) { Value = newKey });
                        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        if (rows <= 0)
                        {
                            throw new InvalidOperationException("Nothing archived (file row not found).");
                        }
                    }

                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }

                    // Revertir archivo si lo movimos
                    if (hadFile && File.Exists(tmpPath))
                    {
                        try
                        {
                            if (File.Exists(srcPath))
                            {
                                try { File.Delete(tmpPath); } catch { /* ignore */ }
                            }
                            else
                            {
                                File.Move(tmpPath, srcPath);
                            }
                        }
                        catch { /* ignore */ }
                    }
                    throw;
                }
            }

            // --- Paso FS B: mover de _archtmp a /archive/... ---
            if (hadFile && File.Exists(tmpPath))
            {
                string finalPath = CombineUnderRoot(newKey);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    if (File.Exists(finalPath))
                    {
                        try { File.Delete(finalPath); }
                        catch { /* ignore */ }
                    }
                    File.Move(tmpPath, finalPath);
                }
                catch (Exception ex)
                {
                    // La fila ya está archivada; reintentará en siguiente corrida si quedó tmp
                    _log.LogError(ex, "Could not move archived file to final location for {FileId}.", c.FileId);
                }
            }
        }

        private async Task UpdateRunAsync(Guid runId, int ok, int fail, string? lastError, CancellationToken ct)
        {
            const string SQL = @"
UPDATE dbo.archive_runs
SET ok_count = @ok,
fail_count = @fail,
last_error = @err,
finished_at_utc = SYSUTCDATETIME()
WHERE run_id = @id;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = runId });
            cmd.Parameters.Add(new SqlParameter("@ok", SqlDbType.Int) { Value = ok });
            cmd.Parameters.Add(new SqlParameter("@fail", SqlDbType.Int) { Value = fail });
            cmd.Parameters.Add(new SqlParameter("@err", SqlDbType.NVarChar, 500) { Value = (object?)lastError ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}


