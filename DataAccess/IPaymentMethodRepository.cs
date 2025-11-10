using System.Data;
using Microsoft.Data.SqlClient;

namespace EPApi.DataAccess
{
    // IPaymentMethodRepository.cs
    public interface IPaymentMethodRepository
    {
        Task UpsertActiveAsync(Guid orgId, string provider, string providerPmId,
            string? brand, string? last4, int? expMonth, int? expYear, string? rawPayload, CancellationToken ct);

        Task<PaymentMethodDto?> GetActiveAsync(Guid orgId, CancellationToken ct);
        Task DeactivateAllAsync(Guid orgId, CancellationToken ct);
        Task<PaymentMethodDto?> GetActiveByOrgAsync(Guid orgId, CancellationToken ct);


    }

    // PaymentMethodDto.cs
    public sealed class PaymentMethodDto
    {
        public Guid Id { get; set; }
        public Guid OrgId { get; set; }
        public string Provider { get; set; } = default!;
        public string ProviderPmId { get; set; } = default!;
        public string? Brand { get; set; }
        public string? Last4 { get; set; }
        public int? ExpMonth { get; set; }
        public int? ExpYear { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    // SqlPaymentMethodRepository.cs
    public sealed class SqlPaymentMethodRepository : IPaymentMethodRepository
    {
        private readonly string _cs;

        public SqlPaymentMethodRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Connection string 'Default' not found.");
        }

        public async Task UpsertActiveAsync(
    Guid orgId,
    string provider,
    string providerPmId,
    string? brand,
    string? last4,
    int? expMonth,
    int? expYear,
    string? rawPayload,
    CancellationToken ct)
        {
            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var tx = (SqlTransaction)await con.BeginTransactionAsync(ct);

            try
            {
                // 0) ¿Existe ya este PM?
                const string sqlFind = @"
SELECT TOP 1 id
FROM dbo.payment_methods
WHERE org_id = @orgId AND provider_pm_id = @pm;
";
                Guid? existingId = null;
                await using (var find = new SqlCommand(sqlFind, con, tx))
                {
                    find.Parameters.Add(new SqlParameter("@orgId", SqlDbType.UniqueIdentifier) { Value = orgId });
                    find.Parameters.Add(new SqlParameter("@pm", SqlDbType.NVarChar, 200) { Value = providerPmId });
                    var obj = await find.ExecuteScalarAsync(ct);
                    if (obj != null && obj != DBNull.Value) existingId = (Guid)obj;
                }

                // 1) Desactivar TODOS los demás (si hay)
                const string sqlDeactivateOthers = @"
UPDATE dbo.payment_methods
SET is_active = 0, updated_at_utc = SYSUTCDATETIME()
WHERE org_id = @orgId
  AND (@existingId IS NULL OR id <> @existingId)
  AND is_active = 1;
";
                await using (var deact = new SqlCommand(sqlDeactivateOthers, con, tx))
                {
                    deact.Parameters.Add(new SqlParameter("@orgId", SqlDbType.UniqueIdentifier) { Value = orgId });
                    deact.Parameters.Add(new SqlParameter("@existingId", SqlDbType.UniqueIdentifier) { Value = (object?)existingId ?? DBNull.Value });
                    await deact.ExecuteNonQueryAsync(ct);
                }

                if (existingId.HasValue)
                {
                    // 2a) Actualiza + Reactiva la fila existente
                    const string sqlUpdate = @"
UPDATE dbo.payment_methods
SET
  provider = @provider,
  brand = @brand,
  last4 = @last4,
  exp_month = @expMonth,
  exp_year = @expYear,
  is_active = 1,
  raw_payload = @raw,
  updated_at_utc = SYSUTCDATETIME()
WHERE id = @id;
";
                    await using (var upd = new SqlCommand(sqlUpdate, con, tx))
                    {
                        upd.Parameters.Add(new SqlParameter("@provider", SqlDbType.NVarChar, 50) { Value = provider });
                        upd.Parameters.Add(new SqlParameter("@brand", SqlDbType.NVarChar, 50) { Value = (object?)brand ?? DBNull.Value });
                        upd.Parameters.Add(new SqlParameter("@last4", SqlDbType.NVarChar, 4) { Value = (object?)last4 ?? DBNull.Value });
                        upd.Parameters.Add(new SqlParameter("@expMonth", SqlDbType.Int) { Value = (object?)expMonth ?? DBNull.Value });
                        upd.Parameters.Add(new SqlParameter("@expYear", SqlDbType.Int) { Value = (object?)expYear ?? DBNull.Value });
                        upd.Parameters.Add(new SqlParameter("@raw", SqlDbType.NVarChar, -1) { Value = (object?)rawPayload ?? DBNull.Value });
                        upd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = existingId.Value });
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                }
                else
                {
                    // 2b) Inserta una nueva fila activa (respetando la UNIQUE org+pm)
                    const string sqlInsert = @"
INSERT INTO dbo.payment_methods
(id, org_id, provider, provider_pm_id, brand, last4, exp_month, exp_year, is_active, raw_payload, created_at_utc, updated_at_utc)
VALUES (NEWID(), @orgId, @provider, @pm, @brand, @last4, @expMonth, @expYear, 1, @raw, SYSUTCDATETIME(), SYSUTCDATETIME());
";
                    await using (var ins = new SqlCommand(sqlInsert, con, tx))
                    {
                        ins.Parameters.Add(new SqlParameter("@orgId", SqlDbType.UniqueIdentifier) { Value = orgId });
                        ins.Parameters.Add(new SqlParameter("@provider", SqlDbType.NVarChar, 50) { Value = provider });
                        ins.Parameters.Add(new SqlParameter("@pm", SqlDbType.NVarChar, 200) { Value = providerPmId });
                        ins.Parameters.Add(new SqlParameter("@brand", SqlDbType.NVarChar, 50) { Value = (object?)brand ?? DBNull.Value });
                        ins.Parameters.Add(new SqlParameter("@last4", SqlDbType.NVarChar, 4) { Value = (object?)last4 ?? DBNull.Value });
                        ins.Parameters.Add(new SqlParameter("@expMonth", SqlDbType.Int) { Value = (object?)expMonth ?? DBNull.Value });
                        ins.Parameters.Add(new SqlParameter("@expYear", SqlDbType.Int) { Value = (object?)expYear ?? DBNull.Value });
                        ins.Parameters.Add(new SqlParameter("@raw", SqlDbType.NVarChar, -1) { Value = (object?)rawPayload ?? DBNull.Value });
                        await ins.ExecuteNonQueryAsync(ct);
                    }
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }


        public async Task<PaymentMethodDto?> GetActiveAsync(Guid orgId, CancellationToken ct)
        {
            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);

            const string sql = @"
SELECT TOP 1
    id,
    org_id,
    provider,
    provider_pm_id,
    brand,
    last4,
    exp_month,
    exp_year,
    is_active,
    created_at_utc,
    updated_at_utc
FROM dbo.payment_methods
WHERE org_id = @orgId AND is_active = 1
ORDER BY updated_at_utc DESC;
";

            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.Add(new SqlParameter("@orgId", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rdr = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await rdr.ReadAsync(ct))
                return null;

            return new PaymentMethodDto
            {
                Id = rdr.GetGuid(rdr.GetOrdinal("id")),
                OrgId = rdr.GetGuid(rdr.GetOrdinal("org_id")),
                Provider = rdr.GetString(rdr.GetOrdinal("provider")),
                ProviderPmId = rdr.GetString(rdr.GetOrdinal("provider_pm_id")),
                Brand = rdr.IsDBNull(rdr.GetOrdinal("brand")) ? null : rdr.GetString(rdr.GetOrdinal("brand")),
                Last4 = rdr.IsDBNull(rdr.GetOrdinal("last4")) ? null : rdr.GetString(rdr.GetOrdinal("last4")),
                ExpMonth = rdr.IsDBNull(rdr.GetOrdinal("exp_month")) ? (int?)null : rdr.GetInt32(rdr.GetOrdinal("exp_month")),
                ExpYear = rdr.IsDBNull(rdr.GetOrdinal("exp_year")) ? (int?)null : rdr.GetInt32(rdr.GetOrdinal("exp_year")),
                IsActive = rdr.GetBoolean(rdr.GetOrdinal("is_active")),
                CreatedAtUtc = rdr.GetDateTime(rdr.GetOrdinal("created_at_utc")),
                UpdatedAtUtc = rdr.GetDateTime(rdr.GetOrdinal("updated_at_utc"))
            };
        }

        public async Task<PaymentMethodDto?> GetActiveByOrgAsync(Guid orgId, CancellationToken ct)
        {
            const string sql = @"
SELECT TOP 1
    id, provider, provider_pm_id, brand, last4, exp_month, exp_year, is_active, created_at_utc, updated_at_utc
FROM dbo.payment_methods
WHERE org_id = @orgId AND is_active = 1
ORDER BY updated_at_utc DESC;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@orgId", orgId);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            return new PaymentMethodDto
            {
                Id = rd.GetGuid(0),
                Provider = rd.GetString(1),
                ProviderPmId = rd.GetString(2),
                Brand = rd.IsDBNull(3) ? null : rd.GetString(3),
                Last4 = rd.IsDBNull(4) ? null : rd.GetString(4),
                ExpMonth = rd.IsDBNull(5) ? (int?)null : rd.GetInt32(5),
                ExpYear = rd.IsDBNull(6) ? (int?)null : rd.GetInt32(6),
                IsActive = rd.GetBoolean(7),
                CreatedAtUtc = rd.GetDateTime(8),
                UpdatedAtUtc = rd.GetDateTime(9),
            };
        }


        public async Task DeactivateAllAsync(Guid orgId, CancellationToken ct)
        {
            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);

            const string sql = @"
UPDATE dbo.payment_methods
SET is_active = 0, updated_at_utc = SYSUTCDATETIME()
WHERE org_id = @orgId AND is_active = 1;
";
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.Add(new SqlParameter("@orgId", SqlDbType.UniqueIdentifier) { Value = orgId });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

}
