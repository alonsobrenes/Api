using EPApi.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace EPApi.DataAccess
{
    public interface IOrgBillingProfileRepository
    {
        Task<BillingProfileDto?> GetAsync(Guid orgId, CancellationToken ct = default);
        Task UpsertAsync(Guid orgId, BillingProfileDto dto, CancellationToken ct = default);
    }

    public sealed  class OrgBillingProfileRepository : IOrgBillingProfileRepository
    {
        private readonly string _cs;
        public OrgBillingProfileRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")!;
        }

        public async Task<BillingProfileDto?> GetAsync(Guid orgId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT TOP 1
  legal_name, trade_name, tax_id,
  contact_email, contact_phone, website,
  bill_line1, bill_line2, bill_city, bill_state_region, bill_postal_code, bill_country_iso2,
  ship_line1, ship_line2, ship_city, ship_state_region, ship_postal_code, ship_country_iso2
FROM dbo.org_billing_profiles
WHERE org_id = @orgId;";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@orgId", orgId);

            await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await rd.ReadAsync(ct)) return null;

            var dto = new BillingProfileDto
            {
                LegalName = rd.GetString(0),
                TradeName = rd.IsDBNull(1) ? null : rd.GetString(1),
                TaxId = rd.GetString(2),
                ContactEmail = rd.GetString(3),
                ContactPhone = rd.IsDBNull(4) ? null : rd.GetString(4),
                Website = rd.IsDBNull(5) ? null : rd.GetString(5),
                BillingAddress = new AddressDto
                {
                    Line1 = rd.GetString(6),
                    Line2 = rd.IsDBNull(7) ? null : rd.GetString(7),
                    City = rd.GetString(8),
                    StateRegion = rd.IsDBNull(9) ? null : rd.GetString(9),
                    PostalCode = rd.GetString(10),
                    CountryIso2 = rd.GetString(11),
                },
                ShippingAddress = rd.IsDBNull(12) ? null : new AddressDto
                {
                    Line1 = rd.GetString(12),
                    Line2 = rd.IsDBNull(13) ? null : rd.GetString(13),
                    City = rd.GetString(14),
                    StateRegion = rd.IsDBNull(15) ? null : rd.GetString(15),
                    PostalCode = rd.GetString(16),
                    CountryIso2 = rd.GetString(17),
                }
            };
            return dto;
        }

        public async Task UpsertAsync(Guid orgId, BillingProfileDto dto, CancellationToken ct = default)
        {
            const string updateSql = @"
UPDATE dbo.org_billing_profiles
SET
  legal_name=@legal_name, trade_name=@trade_name, tax_id=@tax_id,
  contact_email=@contact_email, contact_phone=@contact_phone, website=@website,
  bill_line1=@bill_line1, bill_line2=@bill_line2, bill_city=@bill_city, bill_state_region=@bill_state_region,
  bill_postal_code=@bill_postal_code, bill_country_iso2=@bill_country_iso2,
  ship_line1=@ship_line1, ship_line2=@ship_line2, ship_city=@ship_city, ship_state_region=@ship_state_region,
  ship_postal_code=@ship_postal_code, ship_country_iso2=@ship_country_iso2,
  updated_at_utc = sysutcdatetime()
WHERE org_id=@org_id;";

            const string insertSql = @"
INSERT INTO dbo.org_billing_profiles(
  org_id, legal_name, trade_name, tax_id,
  contact_email, contact_phone, website,
  bill_line1, bill_line2, bill_city, bill_state_region, bill_postal_code, bill_country_iso2,
  ship_line1, ship_line2, ship_city, ship_state_region, ship_postal_code, ship_country_iso2
) VALUES (
  @org_id, @legal_name, @trade_name, @tax_id,
  @contact_email, @contact_phone, @website,
  @bill_line1, @bill_line2, @bill_city, @bill_state_region, @bill_postal_code, @bill_country_iso2,
  @ship_line1, @ship_line2, @ship_city, @ship_state_region, @ship_postal_code, @ship_country_iso2
);";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);

            await using var tx = await con.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = new SqlCommand(updateSql, con, (SqlTransaction)tx))
                {
                    FillParams(cmd, orgId, dto);
                    var rows = await cmd.ExecuteNonQueryAsync(ct);
                    if (rows == 0)
                    {
                        await using var cmdIns = new SqlCommand(insertSql, con, (SqlTransaction)tx);
                        FillParams(cmdIns, orgId, dto);
                        await cmdIns.ExecuteNonQueryAsync(ct);
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

        private static void FillParams(SqlCommand cmd, Guid orgId, BillingProfileDto dto)
        {
            cmd.Parameters.AddWithValue("@org_id", orgId);
            cmd.Parameters.AddWithValue("@legal_name", dto.LegalName);
            cmd.Parameters.AddWithValue("@trade_name", (object?)dto.TradeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tax_id", dto.TaxId);
            cmd.Parameters.AddWithValue("@contact_email", dto.ContactEmail);
            cmd.Parameters.AddWithValue("@contact_phone", (object?)dto.ContactPhone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@website", (object?)dto.Website ?? DBNull.Value);

            var b = dto.BillingAddress ?? new AddressDto();
            cmd.Parameters.AddWithValue("@bill_line1", b.Line1);
            cmd.Parameters.AddWithValue("@bill_line2", (object?)b.Line2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bill_city", b.City);
            cmd.Parameters.AddWithValue("@bill_state_region", (object?)b.StateRegion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bill_postal_code", b.PostalCode);
            cmd.Parameters.AddWithValue("@bill_country_iso2", b.CountryIso2);

            var s = dto.ShippingAddress;
            cmd.Parameters.AddWithValue("@ship_line1", (object?)s?.Line1 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ship_line2", (object?)s?.Line2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ship_city", (object?)s?.City ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ship_state_region", (object?)s?.StateRegion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ship_postal_code", (object?)s?.PostalCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ship_country_iso2", (object?)s?.CountryIso2 ?? DBNull.Value);
        }
    }
}
