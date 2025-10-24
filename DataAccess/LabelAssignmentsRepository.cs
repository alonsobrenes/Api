using EPApi.Utils;
using Microsoft.Data.SqlClient;
using System.Data;

namespace EPApi.DataAccess
{
    public sealed class LabelAssignmentsRepository
    {
        private readonly string _cs;
        public LabelAssignmentsRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string");
        }

        private static bool IsSupportedType(string targetType)
        {
            return SupportedEntityTypes.IsSupported(targetType);
        }

        public async Task<bool> LabelExistsAsync(Guid orgId, int labelId, CancellationToken ct = default)
        {
            const string sql = @"SELECT 1 FROM dbo.labels WHERE org_id=@org AND id=@lid;";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@lid", SqlDbType.Int) { Value = labelId });
            var o = await cmd.ExecuteScalarAsync(ct);
            return o is not null;
        }

        // ===========================================================
        //  GUID-based methods (originales)
        // ===========================================================
        private async Task<bool> TargetExistsAsync(Guid orgId, string targetType, Guid targetId, CancellationToken ct)
        {
            string sql = targetType switch
            {
                "patient" => @"SELECT 1 FROM dbo.patients p
                               JOIN dbo.org_members m ON m.user_id = p.created_by_user_id
                               WHERE p.id=@tid AND m.org_id=@org;",
                "test" => @"SELECT 1 FROM dbo.tests t
                               WHERE t.id=@tid AND t.org_id=@org
                               OR (@org IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.tests') AND name='org_id'));",
                "test_attempt" => @"SELECT 1 FROM dbo.test_attempts a
                               JOIN dbo.patients p ON p.id = a.patient_id
                               JOIN dbo.org_members m ON m.user_id = p.created_by_user_id
                               WHERE a.id=@tid AND m.org_id=@org;",
                "attachment" => @"SELECT 1 FROM dbo.patient_files pf
                         WHERE pf.file_id = @tid
                           AND pf.org_id  = @org
                           AND pf.deleted_at_utc IS NULL;",
                "session" => @"SELECT 1 FROM dbo.patient_sessions s
                         WHERE s.id=@tid AND s.org_id=@org AND s.deleted_at_utc IS NULL;",
                _ => ""
            };

            if (string.IsNullOrEmpty(sql)) return false;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
            var o = await cmd.ExecuteScalarAsync(ct);
            return o is not null;
        }

        public async Task AssignAsync(Guid orgId, int labelId, string targetType, Guid targetId, CancellationToken ct = default)
        {
            if (!IsSupportedType(targetType))
                throw new InvalidOperationException("targetType no soportado");

            const string sql = @"
IF NOT EXISTS (
  SELECT 1 FROM dbo.label_assignments
  WHERE org_id=@org AND label_id=@lid AND target_type=@typ AND target_id_guid=@tid
)
INSERT INTO dbo.label_assignments(org_id, label_id, target_type, target_id_guid, created_at_utc)
VALUES (@org, @lid, @typ, @tid, SYSUTCDATETIME());";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@lid", SqlDbType.Int) { Value = labelId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UnassignAsync(Guid orgId, int labelId, string targetType, Guid targetId, CancellationToken ct = default)
        {
            if (!IsSupportedType(targetType))
                throw new InvalidOperationException("targetType no soportado");

            const string sql = @"
DELETE FROM dbo.label_assignments
WHERE org_id=@org AND label_id=@lid AND target_type=@typ AND target_id_guid=@tid;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@lid", SqlDbType.Int) { Value = labelId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<bool> ValidateAsync(Guid orgId, int labelId, string targetType, Guid targetId, CancellationToken ct)
        {
            if (!IsSupportedType(targetType)) return false;
            if (!await LabelExistsAsync(orgId, labelId, ct)) return false;
            if (!await TargetExistsAsync(orgId, targetType, targetId, ct)) return false;
            return true;
        }

        public async Task<IReadOnlyList<LabelsRepository.LabelRow>> ListForTargetAsync(Guid orgId, string targetType, Guid targetId, bool isOwner, CancellationToken ct = default)
        {
            const string sql = @"
SELECT l.id, l.org_id, l.code, l.name, l.color_hex, l.is_system, l.created_at_utc
FROM dbo.label_assignments la
JOIN dbo.labels l ON l.id = la.label_id AND l.org_id = la.org_id
WHERE (@isOwner = 1 OR la.org_id = @org ) AND la.target_type = @typ AND la.target_id_guid = @tid
ORDER BY l.name;";

            var list = new List<LabelsRepository.LabelRow>(16);
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
            cmd.Parameters.AddWithValue("@isOwner", isOwner ? 1 : 0);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new LabelsRepository.LabelRow
                {
                    Id = rd.GetInt32(0),
                    OrgId = rd.GetGuid(1),
                    Code = rd.GetString(2),
                    Name = rd.GetString(3),
                    ColorHex = rd.GetString(4),
                    IsSystem = rd.GetBoolean(5),
                    CreatedAtUtc = rd.GetDateTime(6)
                });
            }
            return list;
        }

        // ===========================================================
        //  NUEVOS MÉTODOS: variantes INT (para profesionales)
        // ===========================================================

        public async Task<bool> ValidateAsync(Guid orgId, int labelId, string targetType, int targetIdInt, CancellationToken ct)
        {
            if (!IsSupportedType(targetType)) return false;
            if (!await LabelExistsAsync(orgId, labelId, ct)) return false;
            if (!await TargetExistsAsync(orgId, targetType, targetIdInt, ct)) return false;
            return true;
        }

        public async Task<IReadOnlyList<LabelsRepository.LabelRow>> ListForTargetAsync(Guid orgId, string targetType, int targetIdInt, CancellationToken ct = default)
        {
            const string sql = @"
SELECT l.id, l.org_id, l.code, l.name, l.color_hex, l.is_system, l.created_at_utc
FROM dbo.label_assignments la
JOIN dbo.labels l ON l.id = la.label_id AND l.org_id = la.org_id
WHERE la.org_id = @org AND la.target_type = @typ AND la.target_id_int = @uid
ORDER BY l.name;";

            var list = new List<LabelsRepository.LabelRow>(16);
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = targetIdInt });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new LabelsRepository.LabelRow
                {
                    Id = rd.GetInt32(0),
                    OrgId = rd.GetGuid(1),
                    Code = rd.GetString(2),
                    Name = rd.GetString(3),
                    ColorHex = rd.GetString(4),
                    IsSystem = rd.GetBoolean(5),
                    CreatedAtUtc = rd.GetDateTime(6)
                });
            }
            return list;
        }

        private async Task<bool> TargetExistsAsync(Guid orgId, string targetType, int targetIdInt, CancellationToken ct)
        {
            string sql = targetType switch
            {
                "professional" => @"SELECT 1 FROM dbo.users u
                               JOIN dbo.org_members m ON m.user_id = u.id
                               WHERE u.id=@uid AND m.org_id=@org;",                
                _ => ""
            };

            if (string.IsNullOrEmpty(sql)) return false;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = targetIdInt });
            var o = await cmd.ExecuteScalarAsync(ct);
            return o is not null;
        }

        public async Task AssignIntAsync(Guid orgId, int labelId, string targetType, int targetIdInt, CancellationToken ct = default)
        {
            const string sql = @"
IF NOT EXISTS (
  SELECT 1 FROM dbo.label_assignments
  WHERE org_id=@org AND label_id=@lid AND target_type=@typ AND target_id_int=@tid
)
INSERT INTO dbo.label_assignments(org_id, label_id, target_type, target_id_int, created_at_utc)
VALUES (@org, @lid, @typ, @tid, SYSUTCDATETIME());";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@lid", SqlDbType.Int) { Value = labelId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.Int) { Value = targetIdInt });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UnassignIntAsync(Guid orgId, int labelId, string targetType, int targetIdInt, CancellationToken ct = default)
        {
            const string sql = @"
DELETE FROM dbo.label_assignments
WHERE org_id=@org AND label_id=@lid AND target_type=@typ AND target_id_int=@tid;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@lid", SqlDbType.Int) { Value = labelId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.Int) { Value = targetIdInt });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<LabelsRepository.LabelRow>> ListForTargetIntAsync(Guid orgId, string targetType, int targetIdInt, bool isOwner, CancellationToken ct = default)
        {
            const string sql = @"
SELECT l.id, l.org_id, l.code, l.name, l.color_hex, l.is_system, l.created_at_utc
FROM dbo.label_assignments la
JOIN dbo.labels l ON l.id = la.label_id AND l.org_id = la.org_id
WHERE (@isOwner = 1 OR la.org_id = @org ) AND la.target_type = @typ AND la.target_id_int = @tid
ORDER BY l.name;";

            var list = new List<LabelsRepository.LabelRow>(16);
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.Int) { Value = targetIdInt });
            cmd.Parameters.AddWithValue("@isOwner", isOwner ? 1 : 0);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new LabelsRepository.LabelRow
                {
                    Id = rd.GetInt32(0),
                    OrgId = rd.GetGuid(1),
                    Code = rd.GetString(2),
                    Name = rd.GetString(3),
                    ColorHex = rd.GetString(4),
                    IsSystem = rd.GetBoolean(5),
                    CreatedAtUtc = rd.GetDateTime(6)
                });
            }
            return list;
        }
    }
}
