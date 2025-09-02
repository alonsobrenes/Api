using Microsoft.Data.SqlClient;
using System.Data;

namespace EPApi.DataAccess
{
    public sealed class UserDisciplineRepository : IUserDisciplineRepository
    {
        private readonly string _cs;
        public UserDisciplineRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing DefaultConnection");
        }

        public async Task<IReadOnlyList<(int Id, string Code, string Name)>> GetMineAsync(int userId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT d.id, d.code, d.name
FROM dbo.user_disciplines ud
JOIN dbo.disciplines d ON d.id = ud.discipline_id
WHERE ud.user_id = @uid
ORDER BY d.name;";

            var list = new List<(int, string, string)>();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add((rd.GetInt32(0), rd.GetString(1), rd.GetString(2)));

            return list;
        }

        public async Task ReplaceMineAsync(int userId, int[] disciplineIds, CancellationToken ct = default)
        {
            // reemplazo atómico
            const string sql = @"
BEGIN TRAN;
  DELETE FROM dbo.user_disciplines WHERE user_id=@uid;
  /* inserción masiva */
  /* evitamos STRING_SPLIT para compatibilidad */
  /* usamos table-valued parameter */
  INSERT INTO dbo.user_disciplines(user_id, discipline_id, created_at)
  SELECT @uid, x.id, SYSUTCDATETIME()
  FROM @ids AS x
  JOIN dbo.disciplines d ON d.id = x.id;  -- valida que existan
COMMIT TRAN;";

            var tvp = new DataTable();
            tvp.Columns.Add("id", typeof(int));
            foreach (var id in disciplineIds.Distinct())
                tvp.Rows.Add(id);

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

            var p = cmd.Parameters.AddWithValue("@ids", tvp);
            p.SqlDbType = SqlDbType.Structured;
            p.TypeName = "dbo.IntList"; // necesitamos el tipo – lo creamos abajo si no existe
            await EnsureIntListTypeAsync(cn, ct);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task EnsureIntListTypeAsync(SqlConnection cn, CancellationToken ct)
        {
            const string check = "SELECT 1 FROM sys.types WHERE is_table_type=1 AND name='IntList';";
            await using var c1 = new SqlCommand(check, cn);
            var exists = (object?)await c1.ExecuteScalarAsync(ct) != null;
            if (exists) return;

            const string create = "CREATE TYPE dbo.IntList AS TABLE (id INT NOT NULL);";
            await using var c2 = new SqlCommand(create, cn);
            await c2.ExecuteNonQueryAsync(ct);
        }
    }
}
