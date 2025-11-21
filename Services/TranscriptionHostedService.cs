// Services/TranscriptionHostedService.cs
using EPApi.DataAccess;
using EPApi.Services;
using EPApi.Services.Billing;
using EPApi.Services.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Data;

namespace EPApi.Services
{
    public sealed class TranscriptionHostedService : BackgroundService
    {
        private readonly ILogger<TranscriptionHostedService> _log;
        private readonly IConfiguration _cfg;
        private readonly IWebHostEnvironment _env;        
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _idleDelay = TimeSpan.FromSeconds(5);
        private readonly string _connString;

        public TranscriptionHostedService(
            ILogger<TranscriptionHostedService> log,
            IConfiguration cfg,
            IWebHostEnvironment env,            
            IServiceScopeFactory scopeFactory)
        {
            _log = log;
            _cfg = cfg;
            _env = env;            

            _connString = _cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing connection string 'Default'.");
            _scopeFactory = scopeFactory;
        }

        private sealed record PendingTranscript(Guid TranscriptId, Guid InterviewId);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("TranscriptionHostedService iniciado.");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var pending = await TryPickNextTranscriptAsync(stoppingToken);
                    if (pending is null)
                    {
                        await Task.Delay(_idleDelay, stoppingToken);
                        continue;
                    }

                    var (transcriptId, interviewId) = (pending.TranscriptId, pending.InterviewId);

                    using var scope = _scopeFactory.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IInterviewsRepository>();
                    var stt = scope.ServiceProvider.GetRequiredService<ITranscriptionService>();
                    var usage = scope.ServiceProvider.GetRequiredService<IUsageService>();
                    var billing = scope.ServiceProvider.GetRequiredService<BillingRepository>();
                    var fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

                    string? tempFile = null;

                    try
                    {
                        // 1) Resolver ruta local del audio (Blob o legacy /uploads)
                        var latest = await repo.GetLatestAudioAsync(interviewId, stoppingToken);
                        string? absPath = null;

                        if (latest is not null)
                        {
                            var uri = latest.Value.Uri;
                            var mime = latest.Value.Mime;

                            // NUEVO: audio guardado en Blob/Azurite → uri es storageKey tipo "org/{orgId}/interviews/{interviewId}/audio.ext"
                            if (!string.IsNullOrWhiteSpace(uri) &&
                                uri.StartsWith("org/", StringComparison.OrdinalIgnoreCase))
                            {
                                var stream = await fileStorage.OpenReadAsync(uri, stoppingToken);
                                if (stream is null)
                                {
                                    _log.LogWarning(
                                        "Blob de audio no encontrado para entrevista {InterviewId}, storageKey={StorageKey}.",
                                        interviewId, uri);
                                }
                                else
                                {
                                    var tempDir = Path.Combine(_env.ContentRootPath, "temp", "stt");
                                    Directory.CreateDirectory(tempDir);

                                    // Intentar derivar extensión desde el storageKey o el MIME
                                    string ext = Path.GetExtension(uri);
                                    if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(mime))
                                    {
                                        ext = mime switch
                                        {
                                            "audio/webm" => ".webm",
                                            "audio/wav" => ".wav",
                                            "audio/ogg" => ".ogg",
                                            "audio/mpeg" => ".mp3",
                                            "audio/mp4" => ".m4a",
                                            _ => ".audio"
                                        };
                                    }
                                    if (string.IsNullOrWhiteSpace(ext))
                                        ext = ".audio";

                                    tempFile = Path.Combine(
                                        tempDir,
                                        $"{interviewId:N}-{Guid.NewGuid():N}{ext}");

                                    await using (stream)
                                    await using (var fs = System.IO.File.Create(tempFile))
                                    {
                                        await stream.CopyToAsync(fs, stoppingToken);
                                    }

                                    absPath = tempFile;
                                }
                            }
                            // LEGACY: uri tipo "/uploads/interviews/..."
                            else if (!string.IsNullOrWhiteSpace(uri))
                            {
                                var candidate = ResolveAbsoluteUploadPath(uri);
                                if (System.IO.File.Exists(candidate))
                                    absPath = candidate;
                            }
                        }

                        // Fallback extra: carpeta física antigua en /uploads/interviews/{idN}
                        if (absPath is null)
                        {
                            var root = _cfg.GetValue<string>("Uploads:Root")
                                       ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");
                            var folder = Path.Combine(root, "interviews", interviewId.ToString("N"));

                            if (!Directory.Exists(folder) || new DirectoryInfo(folder).GetFiles().Length == 0)
                            {
                                _log.LogWarning("Audio no encontrado para entrevista {InterviewId}.", interviewId);
                                await MarkTranscriptFailedAsync(
                                    transcriptId,
                                    "Audio no encontrado en servidor.",
                                    stoppingToken);
                                await SetStatusSafeAsync(interviewId, "transcribed", stoppingToken);
                                continue;
                            }

                            var files = new DirectoryInfo(folder).GetFiles();
                            Array.Sort(files, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                            absPath = files[0].FullName;
                        }

                        // 2) Ejecutar STT sobre esa ruta local
                        long? durationMs = null;

                        try
                        {
                            var (lang, text, wordsJson, dur) = await stt.TranscribeAsync(absPath!, stoppingToken);
                            durationMs = dur;

                            // Guardar resultado en la misma fila del transcript
                            await CompleteTranscriptAsync(
                                transcriptId,
                                lang ?? "es",
                                text,
                                wordsJson,
                                stoppingToken);

                            // Macro: marcar entrevista como transcribed
                            await SetStatusSafeAsync(interviewId, "transcribed", stoppingToken);

                            // 3) Debitar minutos (no rompe si falla)
                            try
                            {
                                int? clinicianUserId = await GetClinicianUserIdAsync(interviewId, stoppingToken);
                                if (clinicianUserId.HasValue && durationMs.HasValue)
                                {
                                    var orgId = await billing.GetOrgIdForUserAsync(clinicianUserId.Value, stoppingToken);
                                    if (orgId.HasValue)
                                    {
                                        int minutes = Math.Max(
                                            1,
                                            (int)TimeSpan.FromMilliseconds(durationMs.Value).TotalMinutes);
                                        var idemKey = $"transcript:{transcriptId}:{orgId.Value}";
                                        var gate = await usage.TryConsumeAsync(
                                            orgId.Value,
                                            "stt.minutes.monthly",
                                            minutes,
                                            idemKey,
                                            stoppingToken);

                                        if (!gate.Allowed)
                                        {
                                            _log.LogWarning(
                                                "Límite mensual de STT alcanzado para org {OrgId}.",
                                                orgId.Value);
                                        }
                                    }
                                }
                            }
                            catch (Exception exUsage)
                            {
                                _log.LogWarning(
                                    exUsage,
                                    "No se pudo registrar consumo de minutos STT para {TranscriptId}.",
                                    transcriptId);
                                // No rompemos la transcripción por fallo de métricas
                            }

                            _log.LogInformation(
                                "Transcripción OK para transcript {TranscriptId}, entrevista {InterviewId}.",
                                transcriptId, interviewId);
                        }
                        catch (Exception exStt)
                        {
                            _log.LogError(
                                exStt,
                                "Error al transcribir entrevista {InterviewId} para transcript {TranscriptId}.",
                                interviewId, transcriptId);

                            await MarkTranscriptFailedAsync(
                                transcriptId,
                                "Error al transcribir el audio. Intenta de nuevo más tarde.",
                                stoppingToken);

                            await SetStatusSafeAsync(interviewId, "transcribed", stoppingToken);
                        }
                    }
                    finally
                    {
                        if (tempFile is not null)
                        {
                            try { System.IO.File.Delete(tempFile); }
                            catch { /* noop */ }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error en ciclo de TranscriptionHostedService.");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }

            _log.LogInformation("TranscriptionHostedService detenido.");
        }

        private async Task<PendingTranscript?> TryPickNextTranscriptAsync(CancellationToken ct)
        {
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            // 1) leer candidato
            Guid? transcriptId = null;
            Guid? interviewId = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT TOP 1 t.id, t.interview_id
FROM dbo.interview_transcripts t WITH (READPAST)
WHERE t.status = N'queued'
ORDER BY t.created_at_utc ASC;";
                using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    transcriptId = rd.GetGuid(0);
                    interviewId = rd.GetGuid(1);
                }
            }
            if (transcriptId is null) return null;

            // 2) claim atómico: pasar a processing + started_at_utc y aumentar attempt_count
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE dbo.interview_transcripts
SET status = N'processing',
    started_at_utc = SYSUTCDATETIME(),
    attempt_count = attempt_count + 1
WHERE id = @id AND status = N'queued';";
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = transcriptId.Value });
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows == 0) return null; // carrera: otro worker lo tomó
            }

            // (macro) marcar entrevista como 'transcribing'
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE dbo.interviews SET status = N'transcribing' WHERE id = @iid;";
                cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId!.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return new PendingTranscript(transcriptId.Value, interviewId!.Value);
        }

        private async Task CompleteTranscriptAsync(Guid transcriptId, string language, string text, string? wordsJson, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.interview_transcripts
SET [language] = @lang,
    [text] = @text,
    [words_json] = @words,
    [finished_at_utc] = SYSUTCDATETIME(),
    [status] = N'done'
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = transcriptId });
            cmd.Parameters.Add(new SqlParameter("@lang", SqlDbType.NVarChar, 8) { Value = (object)language ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@text", SqlDbType.NVarChar) { Value = (object)text ?? "" });
            cmd.Parameters.Add(new SqlParameter("@words", SqlDbType.NVarChar) { Value = (object?)wordsJson ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task MarkTranscriptFailedAsync(Guid transcriptId, string errorMessage, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.interview_transcripts
SET [status] = N'failed',
    [error_message] = @err,
    [finished_at_utc] = SYSUTCDATETIME()
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = transcriptId });
            cmd.Parameters.Add(new SqlParameter("@err", SqlDbType.NVarChar) { Value = (object)errorMessage ?? "" });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task SetStatusSafeAsync(Guid interviewId, string status, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInterviewsRepository>();
            try { await repo.SetStatusAsync(interviewId, status, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Falló SetStatusAsync({InterviewId}, {Status})", interviewId, status); }
        }

        private async Task<int?> GetClinicianUserIdAsync(Guid interviewId, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT clinician_user_id FROM dbo.interviews WHERE id=@id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = interviewId });
            var raw = await cmd.ExecuteScalarAsync(ct);
            if (raw == null || raw == DBNull.Value) return null;
            return Convert.ToInt32(raw);
        }

        private string ResolveAbsoluteUploadPath(string uri)
        {
            var root = _cfg.GetValue<string>("Uploads:Root")
                       ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");

            string cleaned = (uri ?? "").Replace('\\', '/');
            if (cleaned.StartsWith("/")) cleaned = cleaned.Substring(1);
            if (cleaned.StartsWith("uploads/")) cleaned = cleaned.Substring("uploads/".Length);

            var abs = Path.Combine(root, cleaned.Replace('/', Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(abs))
            {
                var webroot = Path.Combine(_env.ContentRootPath, "wwwroot");
                var alt = Path.Combine(webroot, cleaned.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(alt)) return alt;
            }
            return abs;
        }
    }
}
