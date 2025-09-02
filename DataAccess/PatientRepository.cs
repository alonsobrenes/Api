using System.Data;
using EPApi.Models;
using Microsoft.Data.SqlClient;

namespace EPApi.DataAccess
{
    public sealed class PatientRepository : IPatientRepository
    {
        private readonly string _cs;
        public PatientRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string");
        }

        // ============================================================
        // LISTAR (Compatibilidad: firma antigua)
        // ============================================================
        public async Task<(IReadOnlyList<PatientListItem> Items, int Total)> GetPagedAsync(
          int page, int pageSize, string? search, bool? active, CancellationToken ct = default)
        {
            // Sin filtro de propietario (por compatibilidad)
            return await GetPagedAsync(page, pageSize, search, active, ownerUserId: null, isAdmin: true, ct);
        }

        // ============================================================
        // LISTAR (Nueva firma con propietario)
        // ============================================================
        public async Task<(IReadOnlyList<PatientListItem> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 25;

            // Si es admin, no filtramos por owner (equivale a null)
            int? ownerParam = isAdmin ? null : ownerUserId;

            const string SQL = @"
;WITH T AS (
  SELECT
    p.id,
    p.identification_type, p.identification_number,
    p.first_name, p.last_name1, p.last_name2,
    p.date_of_birth, p.sex,
    p.contact_email, p.contact_phone,
    p.is_active, p.created_at, p.updated_at
  FROM dbo.patients p
  WHERE (@active IS NULL OR p.is_active = @active)
    AND (@owner IS NULL OR p.created_by_user_id = @owner)
    AND (
      @search IS NULL OR @search = '' OR
      p.identification_number LIKE '%' + @search + '%' OR
      p.first_name LIKE '%' + @search + '%' OR
      p.last_name1 LIKE '%' + @search + '%' OR
      (p.last_name2 IS NOT NULL AND p.last_name2 LIKE '%' + @search + '%')
    )
)
SELECT COUNT(*) FROM T;

WITH T AS (
  SELECT
    p.id,
    p.identification_type, p.identification_number,
    p.first_name, p.last_name1, p.last_name2,
    p.date_of_birth, p.sex,
    p.contact_email, p.contact_phone,
    p.is_active, p.created_at, p.updated_at
  FROM dbo.patients p
  WHERE (@active IS NULL OR p.is_active = @active)
    AND (@owner IS NULL OR p.created_by_user_id = @owner)
    AND (
      @search IS NULL OR @search = '' OR
      p.identification_number LIKE '%' + @search + '%' OR
      p.first_name LIKE '%' + @search + '%' OR
      p.last_name1 LIKE '%' + @search + '%' OR
      (p.last_name2 IS NOT NULL AND p.last_name2 LIKE '%' + @search + '%')
    )
)
SELECT *
FROM T
ORDER BY last_name1, last_name2, first_name
OFFSET (@off) ROWS FETCH NEXT (@ps) ROWS ONLY;";

            var list = new List<PatientListItem>();
            var total = 0;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@search", SqlDbType.NVarChar, 255) { Value = (object?)search ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = (object?)active ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@owner", SqlDbType.Int) { Value = (object?)ownerParam ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@off", SqlDbType.Int) { Value = (page - 1) * pageSize });
            cmd.Parameters.Add(new SqlParameter("@ps", SqlDbType.Int) { Value = pageSize });

            await using var rd = await cmd.ExecuteReaderAsync(ct);

            if (await rd.ReadAsync(ct))
                total = rd.GetInt32(0);

            if (await rd.NextResultAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    list.Add(new PatientListItem
                    {
                        Id = rd.GetGuid(0),
                        IdentificationType = rd.GetString(1),
                        IdentificationNumber = rd.GetString(2),
                        FirstName = rd.GetString(3),
                        LastName1 = rd.GetString(4),
                        LastName2 = rd.IsDBNull(5) ? null : rd.GetString(5),
                        DateOfBirth = rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                        Sex = rd.IsDBNull(7) ? null : rd.GetString(7),
                        ContactEmail = rd.IsDBNull(8) ? null : rd.GetString(8),
                        ContactPhone = rd.IsDBNull(9) ? null : rd.GetString(9),
                        IsActive = rd.GetBoolean(10),
                        CreatedAt = rd.GetDateTime(11),
                        UpdatedAt = rd.GetDateTime(12),
                    });
                }
            }

            return (list, total);
        }

        // ============================================================
        // GET BY ID (Compat)
        // ============================================================
        public async Task<PatientListItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            // Sin filtro de owner (compatibilidad)
            return await GetByIdAsync(id, ownerUserId: null, isAdmin: true, ct);
        }

        // ============================================================
        // GET BY ID (Nueva firma con propietario)
        // ============================================================
        public async Task<PatientListItem?> GetByIdAsync(Guid id, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            int? ownerParam = isAdmin ? null : ownerUserId;

            const string SQL = @"
SELECT
  p.id,
  p.identification_type, p.identification_number,
  p.first_name, p.last_name1, p.last_name2,
  p.date_of_birth, p.sex,
  p.contact_email, p.contact_phone,
  p.is_active, p.created_at, p.updated_at
FROM dbo.patients p
WHERE p.id = @id
  AND (@owner IS NULL OR p.created_by_user_id = @owner);";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@owner", SqlDbType.Int) { Value = (object?)ownerParam ?? DBNull.Value });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            return new PatientListItem
            {
                Id = rd.GetGuid(0),
                IdentificationType = rd.GetString(1),
                IdentificationNumber = rd.GetString(2),
                FirstName = rd.GetString(3),
                LastName1 = rd.GetString(4),
                LastName2 = rd.IsDBNull(5) ? null : rd.GetString(5),
                DateOfBirth = rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                Sex = rd.IsDBNull(7) ? null : rd.GetString(7),
                ContactEmail = rd.IsDBNull(8) ? null : rd.GetString(8),
                ContactPhone = rd.IsDBNull(9) ? null : rd.GetString(9),
                IsActive = rd.GetBoolean(10),
                CreatedAt = rd.GetDateTime(11),
                UpdatedAt = rd.GetDateTime(12),
            };
        }

        // ============================================================
        // CREATE (Compat)
        // ============================================================
        public async Task<Guid> CreateAsync(PatientCreateDto dto, CancellationToken ct = default)
        {
            // Compatibilidad: si no nos pasan owner, lo dejamos NULL (o setéalo a un admin si quieres)
            return await CreateAsync(dto, ownerUserId: 0, setOwner: false, ct);
        }

        // ============================================================
        // CREATE (Nueva firma con propietario)
        // ============================================================
        public async Task<Guid> CreateAsync(PatientCreateDto dto, int ownerUserId, CancellationToken ct = default)
        {
            return await CreateAsync(dto, ownerUserId, setOwner: true, ct);
        }

        private async Task<Guid> CreateAsync(PatientCreateDto dto, int ownerUserId, bool setOwner, CancellationToken ct)
        {
            const string SQL = @"
INSERT INTO dbo.patients
  (id, identification_type, identification_number,
   first_name, last_name1, last_name2,
   date_of_birth, sex, contact_email, contact_phone,
   is_active, created_at, updated_at, created_by_user_id)
VALUES
  (@id, @type, @num, @first, @last1, @last2,
   @dob, @sex, @email, @phone, @active, SYSUTCDATETIME(), SYSUTCDATETIME(), @owner);";

            var id = Guid.NewGuid();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.AddRange(new[]
            {
                new SqlParameter("@id", SqlDbType.UniqueIdentifier){ Value = id },
                new SqlParameter("@type", SqlDbType.NVarChar, 20){ Value = dto.IdentificationType },
                new SqlParameter("@num", SqlDbType.NVarChar, 50){ Value = dto.IdentificationNumber },
                new SqlParameter("@first", SqlDbType.NVarChar, 100){ Value = dto.FirstName },
                new SqlParameter("@last1", SqlDbType.NVarChar, 100){ Value = dto.LastName1 },
                new SqlParameter("@last2", SqlDbType.NVarChar, 100){ Value = (object?)dto.LastName2 ?? DBNull.Value },
                new SqlParameter("@dob", SqlDbType.DateTime2){ Value = (object?)dto.DateOfBirth ?? DBNull.Value },
                new SqlParameter("@sex", SqlDbType.NVarChar, 10){ Value = (object?)dto.Sex ?? DBNull.Value },
                new SqlParameter("@email", SqlDbType.NVarChar, 255){ Value = (object?)dto.ContactEmail ?? DBNull.Value },
                new SqlParameter("@phone", SqlDbType.NVarChar, 50){ Value = (object?)dto.ContactPhone ?? DBNull.Value },
                new SqlParameter("@active", SqlDbType.Bit){ Value = dto.IsActive },
                new SqlParameter("@owner", SqlDbType.Int){ Value = setOwner ? ownerUserId : DBNull.Value },
            });
            await cmd.ExecuteNonQueryAsync(ct);
            return id;
        }

        // ============================================================
        // UPDATE (Compat)
        // ============================================================
        public async Task<bool> UpdateAsync(Guid id, PatientUpdateDto dto, CancellationToken ct = default)
        {
            // Compatibilidad: sin filtro de propietario
            return await UpdateAsync(id, dto, ownerUserId: null, isAdmin: true, ct);
        }

        // ============================================================
        // UPDATE (Nueva firma con propietario)
        // ============================================================
        public async Task<bool> UpdateAsync(Guid id, PatientUpdateDto dto, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            int? ownerParam = isAdmin ? null : ownerUserId;

            const string SQL = @"
UPDATE p
SET identification_type = @type,
    identification_number = @num,
    first_name = @first,
    last_name1 = @last1,
    last_name2 = @last2,
    date_of_birth = @dob,
    sex = @sex,
    contact_email = @email,
    contact_phone = @phone,
    is_active = @active,
    updated_at = SYSUTCDATETIME()
FROM dbo.patients p
WHERE p.id = @id
  AND (@owner IS NULL OR p.created_by_user_id = @owner);";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.AddRange(new[]
            {
                new SqlParameter("@id", SqlDbType.UniqueIdentifier){ Value = id },
                new SqlParameter("@type", SqlDbType.NVarChar, 20){ Value = dto.IdentificationType },
                new SqlParameter("@num", SqlDbType.NVarChar, 50){ Value = dto.IdentificationNumber },
                new SqlParameter("@first", SqlDbType.NVarChar, 100){ Value = dto.FirstName },
                new SqlParameter("@last1", SqlDbType.NVarChar, 100){ Value = dto.LastName1 },
                new SqlParameter("@last2", SqlDbType.NVarChar, 100){ Value = (object?)dto.LastName2 ?? DBNull.Value },
                new SqlParameter("@dob", SqlDbType.DateTime2){ Value = (object?)dto.DateOfBirth ?? DBNull.Value },
                new SqlParameter("@sex", SqlDbType.NVarChar, 10){ Value = (object?)dto.Sex ?? DBNull.Value },
                new SqlParameter("@email", SqlDbType.NVarChar, 255){ Value = (object?)dto.ContactEmail ?? DBNull.Value },
                new SqlParameter("@phone", SqlDbType.NVarChar, 50){ Value = (object?)dto.ContactPhone ?? DBNull.Value },
                new SqlParameter("@active", SqlDbType.Bit){ Value = dto.IsActive },
                new SqlParameter("@owner", SqlDbType.Int){ Value = (object?)ownerParam ?? DBNull.Value },
            });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n > 0;
        }

        // ============================================================
        // DELETE (Compat)
        // ============================================================
        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            // Compatibilidad: sin filtro de propietario
            return await DeleteAsync(id, ownerUserId: null, isAdmin: true, ct);
        }

        // ============================================================
        // DELETE (Nueva firma con propietario)
        // ============================================================
        public async Task<bool> DeleteAsync(Guid id, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            int? ownerParam = isAdmin ? null : ownerUserId;

            const string SQL = @"
DELETE p
FROM dbo.patients p
WHERE p.id = @id
  AND (@owner IS NULL OR p.created_by_user_id = @owner);";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@owner", SqlDbType.Int) { Value = (object?)ownerParam ?? DBNull.Value });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n > 0;
        }
    }
}
