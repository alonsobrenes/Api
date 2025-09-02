using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using EPApi.Models;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // profesional autenticado
    public sealed class AssignmentsController : ControllerBase
    {
        private readonly string _cs;
        public AssignmentsController(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Missing DefaultConnection");
        }

        private int GetUserId()
        {
            var sid = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            return int.TryParse(sid, out var n) ? n : throw new InvalidOperationException("Invalid user id in token.");
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] AssignmentCreateDto dto, CancellationToken ct)
        {
            if (dto.TestId == Guid.Empty || dto.PatientId == Guid.Empty)
                return BadRequest("TestId y PatientId son obligatorios.");

            const string sql = @"
INSERT INTO dbo.test_assignments
  (id, test_id, batch_id, assigned_to_user_id, subject_user_id, subject_patient_id,
   respondent_role, relation_label, status, assigned_at, due_at, completed_at)
VALUES
  (@id, @tid, NULL, @assignee, NULL, @pid, @role, @rel, 'pending', SYSDATETIME(), @due, NULL);";

            var id = Guid.NewGuid();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddRange(new[]
            {
        new SqlParameter("@id",       SqlDbType.UniqueIdentifier){ Value = id },
        new SqlParameter("@tid",      SqlDbType.UniqueIdentifier){ Value = dto.TestId },
        new SqlParameter("@assignee", SqlDbType.Int){ Value = GetUserId() }, // el profesional “entrega” el iPad
        new SqlParameter("@pid",      SqlDbType.UniqueIdentifier){ Value = dto.PatientId },
        new SqlParameter("@role",     SqlDbType.NVarChar, 20){ Value = (object?)dto.RespondentRole ?? DBNull.Value },
        new SqlParameter("@rel",      SqlDbType.NVarChar, 100){ Value = (object?)dto.RelationLabel ?? DBNull.Value },
        new SqlParameter("@due",      SqlDbType.DateTime2){ Value = (object?)dto.DueAt ?? DBNull.Value },
      });
            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new AssignmentCreatedDto { Id = id, Status = "pending" });
        }
    }
}
