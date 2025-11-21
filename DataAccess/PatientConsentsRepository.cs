using EPApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.DataAccess
{
    public sealed class PatientConsentsRepository : IPatientConsentsRepository
    {
        private readonly string _connString;

        public PatientConsentsRepository(IConfiguration config)
        {
            _connString = config.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<PatientConsentDto?> GetLatestAsync(
            Guid patientId,
            string consentType,
            CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT TOP (1)
    id,
    patient_id,
    consent_type,
    consent_version,
    local_addendum_country,
    local_addendum_version,
    country_code,
    language,
    signed_name,
    signed_id_number,
    signed_by_relationship,
    signed_at_utc,
    revoked_at_utc,
    signature_uri,
    created_by_user_id,
    created_at_utc,
    ip_address,
    user_agent,
    raw_consent_text
FROM dbo.patient_consents
WHERE patient_id = @patient
  AND consent_type = @ctype
ORDER BY signed_at_utc DESC;";
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@ctype", SqlDbType.NVarChar, 50) { Value = consentType });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                return null;

            return MapRow(rd);
        }

        public async Task<IReadOnlyList<PatientConsentDto>> GetHistoryAsync(
            Guid patientId,
            string consentType,
            CancellationToken ct = default)
        {
            var list = new List<PatientConsentDto>();

            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    id,
    patient_id,
    consent_type,
    consent_version,
    local_addendum_country,
    local_addendum_version,
    country_code,
    language,
    signed_name,
    signed_id_number,
    signed_by_relationship,
    signed_at_utc,
    revoked_at_utc,
    signature_uri,
    created_by_user_id,
    created_at_utc,
    ip_address,
    user_agent,
    raw_consent_text
FROM dbo.patient_consents
WHERE patient_id = @patient
  AND consent_type = @ctype
ORDER BY signed_at_utc DESC;";
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@ctype", SqlDbType.NVarChar, 50) { Value = consentType });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(MapRow(rd));
            }

            return list;
        }

        public async Task<Guid> CreateAsync(
            Guid patientId,
            int createdByUserId,
            string consentType,
            string consentVersion,
            string? localAddendumCountry,
            string? localAddendumVersion,
            string? countryCode,
            string? language,
            string signedName,
            string? signedIdNumber,
            string signedByRelationship,
            string? signatureUri,
            string? ipAddress,
            string? userAgent,
            string? rawConsentText,
            CancellationToken ct = default)
        {
            var id = Guid.NewGuid();

            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.patient_consents (
    id,
    patient_id,
    consent_type,
    consent_version,
    local_addendum_country,
    local_addendum_version,
    country_code,
    language,
    signed_name,
    signed_id_number,
    signed_by_relationship,
    signed_at_utc,
    revoked_at_utc,
    signature_uri,
    created_by_user_id,
    created_at_utc,
    ip_address,
    user_agent,
    raw_consent_text
)
VALUES (
    @id,
    @patient,
    @ctype,
    @cver,
    @lacountry,
    @laver,
    @ccode,
    @lang,
    @sname,
    @sid,
    @srel,
    SYSUTCDATETIME(),
    NULL,
    @suri,
    @cuid,
    SYSUTCDATETIME(),
    @ip,
    @agent,
    @raw
);";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@ctype", SqlDbType.NVarChar, 50) { Value = consentType });
            cmd.Parameters.Add(new SqlParameter("@cver", SqlDbType.NVarChar, 50) { Value = consentVersion });
            cmd.Parameters.Add(new SqlParameter("@lacountry", SqlDbType.NVarChar, 2) { Value = (object?)localAddendumCountry ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@laver", SqlDbType.NVarChar, 50) { Value = (object?)localAddendumVersion ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@ccode", SqlDbType.NVarChar, 2) { Value = (object?)countryCode ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@lang", SqlDbType.NVarChar, 10) { Value = (object?)language ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@sname", SqlDbType.NVarChar, 200) { Value = signedName });
            cmd.Parameters.Add(new SqlParameter("@sid", SqlDbType.NVarChar, 50) { Value = (object?)signedIdNumber ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@srel", SqlDbType.NVarChar, 30) { Value = signedByRelationship });
            cmd.Parameters.Add(new SqlParameter("@suri", SqlDbType.NVarChar, -1) { Value = (object?)signatureUri ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@cuid", SqlDbType.Int) { Value = createdByUserId });
            cmd.Parameters.Add(new SqlParameter("@ip", SqlDbType.NVarChar, 64) { Value = (object?)ipAddress ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@agent", SqlDbType.NVarChar, 400) { Value = (object?)userAgent ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@raw", SqlDbType.NVarChar) { Value = (object?)rawConsentText ?? DBNull.Value });

            await cmd.ExecuteNonQueryAsync(ct);
            return id;
        }

        public async Task<bool> UpdateSignatureUriAsync(
    Guid consentId,
    string signatureUri,
    CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.patient_consents
SET signature_uri = @suri
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = consentId });
            cmd.Parameters.Add(new SqlParameter("@suri", SqlDbType.NVarChar, -1) { Value = signatureUri });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        // -------------------------------------------------------------

        private static PatientConsentDto MapRow(SqlDataReader rd)
        {
            return new PatientConsentDto
            {
                Id = rd.GetGuid(0),
                PatientId = rd.GetGuid(1),
                ConsentType = rd.GetString(2),
                ConsentVersion = rd.GetString(3),
                LocalAddendumCountry = rd.IsDBNull(4) ? null : rd.GetString(4),
                LocalAddendumVersion = rd.IsDBNull(5) ? null : rd.GetString(5),
                CountryCode = rd.IsDBNull(6) ? null : rd.GetString(6),
                Language = rd.IsDBNull(7) ? null : rd.GetString(7),
                SignedName = rd.GetString(8),
                SignedIdNumber = rd.IsDBNull(9) ? null : rd.GetString(9),
                SignedByRelationship = rd.GetString(10),
                SignedAtUtc = rd.GetDateTime(11),
                RevokedAtUtc = rd.IsDBNull(12) ? (DateTime?)null : rd.GetDateTime(12),
                SignatureUri = rd.IsDBNull(13) ? null : rd.GetString(13),
                CreatedByUserId = rd.GetInt32(14),
                CreatedAtUtc = rd.GetDateTime(15),
                IpAddress = rd.IsDBNull(16) ? null : rd.GetString(16),
                UserAgent = rd.IsDBNull(17) ? null : rd.GetString(17),
                RawConsentText = rd.IsDBNull(18) ? null : rd.GetString(18),
            };
        }
    }
}
