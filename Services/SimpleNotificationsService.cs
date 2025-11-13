using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EPApi.Services
{
    public class SimpleNotificationsService : ISimpleNotificationsService
    {
        private readonly string _cs;

        public SimpleNotificationsService(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<Guid> CreateForUserAsync(
            int userId,
            string title,
            string body,
            string kind,
            string? actionUrl,
            string? actionLabel,
            int? createdByUserId = null,
            CancellationToken ct = default)
        {
            var notifId = Guid.NewGuid();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                // 1) Insert en notifications
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqlTransaction)tx;
                    cmd.CommandText = @"
INSERT INTO dbo.notifications
  (id, org_id, title, body, kind, audience, audience_value, published_at_utc, expires_at_utc,
   created_by_user_id, created_at_utc, updated_at_utc, action_url, action_label)
VALUES
  (@id, NULL, @title, @body, @kind, N'user', @aud, SYSUTCDATETIME(), NULL,
   @createdBy, SYSUTCDATETIME(), NULL, @url, @label);";

                    cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = notifId });
                    cmd.Parameters.Add(new SqlParameter("@title", SqlDbType.NVarChar, 200) { Value = title });
                    cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.NVarChar, -1) { Value = body });
                    cmd.Parameters.Add(new SqlParameter("@kind", SqlDbType.NVarChar, 16) { Value = kind });
                    // audience_value: guardamos el userId como string (tu columna es NVARCHAR(MAX))
                    cmd.Parameters.Add(new SqlParameter("@aud", SqlDbType.NVarChar, -1) { Value = userId.ToString() });
                    cmd.Parameters.Add(new SqlParameter("@createdBy", SqlDbType.Int) { Value = (object?)createdByUserId ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@url", SqlDbType.NVarChar, 500) { Value = (object?)actionUrl ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@label", SqlDbType.NVarChar, 80) { Value = (object?)actionLabel ?? DBNull.Value });

                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 2) Enlazar al usuario en user_notifications
                await using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.Transaction = (SqlTransaction)tx;
                    cmd2.CommandText = @"
INSERT INTO dbo.user_notifications
  (notification_id, user_id, is_read, read_at_utc, archived_at_utc, created_at_utc)
VALUES
  (@id, @uid, 0, NULL, NULL, SYSUTCDATETIME());";

                    cmd2.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = notifId });
                    cmd2.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

                    await cmd2.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return notifId;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        // SimpleNotificationsService.cs (agrega este método)
        public async Task<Guid> CreateForOrgAsync(
            int orgId,
            string title,
            string body,
            string kind,
            string? actionUrl,
            string? actionLabel,
            int? createdByUserId = null,
            CancellationToken ct = default)
        {
            var notifId = Guid.NewGuid();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                // 1) notifications (audience=org)
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqlTransaction)tx;
                    cmd.CommandText = @"
INSERT INTO dbo.notifications
  (id, org_id, title, body, kind, audience, audience_value, published_at_utc, expires_at_utc,
   created_by_user_id, created_at_utc, updated_at_utc, action_url, action_label)
VALUES
  (@id, @org, @title, @body, @kind, N'org', CAST(@org AS nvarchar(max)), SYSUTCDATETIME(), NULL,
   @createdBy, SYSUTCDATETIME(), NULL, @url, @label);";
                    cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = notifId });
                    cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.Int) { Value = orgId });
                    cmd.Parameters.Add(new SqlParameter("@title", SqlDbType.NVarChar, 200) { Value = title });
                    cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.NVarChar, -1) { Value = body });
                    cmd.Parameters.Add(new SqlParameter("@kind", SqlDbType.NVarChar, 16) { Value = kind });
                    cmd.Parameters.Add(new SqlParameter("@createdBy", SqlDbType.Int) { Value = (object?)createdByUserId ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@url", SqlDbType.NVarChar, 500) { Value = (object?)actionUrl ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@label", SqlDbType.NVarChar, 80) { Value = (object?)actionLabel ?? DBNull.Value });
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 2) fanout a user_notifications: asumo users.id INT y users.org_id INT (coincide con tu MSC #42)
                await using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.Transaction = (SqlTransaction)tx;
                    cmd2.CommandText = @"
INSERT INTO dbo.user_notifications (notification_id, user_id, is_read, read_at_utc, archived_at_utc, created_at_utc)
SELECT @id, u.id, 0, NULL, NULL, SYSUTCDATETIME()
FROM dbo.users AS u
WHERE u.org_id = @org;";
                    cmd2.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = notifId });
                    cmd2.Parameters.Add(new SqlParameter("@org", SqlDbType.Int) { Value = orgId });
                    await cmd2.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return notifId;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

    }
}
