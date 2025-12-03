using Microsoft.Data.SqlClient;

namespace EPApi.DataAccess
{
    public class OrgRepository : IOrgRepository
    {
        private readonly string _cs;
        public OrgRepository(IConfiguration cfg) => _cs = cfg.GetConnectionString("Default")!;

        public async Task<int> CountActiveMembersAsync(Guid orgId, CancellationToken ct)
        {            
            const string sql = @"
SELECT COUNT(*) 
FROM dbo.org_members m
WHERE m.org_id = @orgId
  AND (m.role IN (N'owner', N'editor'))
  AND (m.disabled_at_utc IS NULL OR m.disabled_at_utc = '0001-01-01') -- si usas datetime2 default
  AND (CASE WHEN COL_LENGTH('dbo.org_members','is_active') IS NOT NULL 
            THEN CASE WHEN m.status = 'active' THEN 1 ELSE 0 END 
            ELSE 1 END) = 1;";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@orgId", orgId);
            var o = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(o);
        }

        public async Task<string?> GetLogoUrlAsync(Guid orgId, CancellationToken ct)
        {
            const string sql = @"
        SELECT logo_url
        FROM orgs
        WHERE id = @orgId";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@orgId", orgId);
            var o = await cmd.ExecuteScalarAsync(ct);
            return o as string;
        }

        public async Task UpdateLogoUrlAsync(Guid orgId, string? logoUrl, CancellationToken ct)
        {
            const string sql = @"
        UPDATE orgs
        SET logo_url = @logoUrl
        WHERE id = @orgId";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@orgId", orgId);
            cmd.Parameters.AddWithValue("@logoUrl", logoUrl);
            await cmd.ExecuteNonQueryAsync(ct);            
        }

    }
}
