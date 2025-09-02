// Controllers/ClinicianPatientsController.cs
using System.Security.Claims;
using EPApi.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/clinician/patients")]
    [Authorize]
    public sealed class ClinicianPatientsController : ControllerBase
    {
        private readonly IClinicianReviewRepository _repo;
        public ClinicianPatientsController(IClinicianReviewRepository repo) => _repo = repo;

        private int? GetCurrentUserId()
        {
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub");
            return int.TryParse(raw, out var id) ? id : null;
        }

        private bool IsAdmin()
        {
            if (User.IsInRole("Admin")) return true;
            var role = User.FindFirstValue(ClaimTypes.Role);
            return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
        }

        // GET /api/clinician/patients/recent?take=5
        [HttpGet("recent")]
        public async Task<IActionResult> GetRecent([FromQuery] int take = 5, CancellationToken ct = default)
        {
            var ownerUserId = GetCurrentUserId();   // reutiliza tu helper existente
            var isAdmin = IsAdmin();                // idem
            var items = await _repo.ListRecentPatientsAsync(ownerUserId, isAdmin, take, ct);
            return Ok(items); // lista simple [{ id, firstName, lastName1, ... }]
        }


        /// <summary>
        /// Lista intentos/evaluaciones registradas para un paciente.
        /// </summary>
        /// GET /api/clinician/patients/{patientId}/assessments
        /// (Alias legacy) GET /api/clinician/patients/{patientId}/attempts
        [HttpGet("{patientId:guid}/assessments")]
        [HttpGet("{patientId:guid}/attempts")] // alias de compatibilidad
        public async Task<IActionResult> GetAssessments(Guid patientId, CancellationToken ct)
        {
            var ownerUserId = GetCurrentUserId();
            var isAdmin = IsAdmin();

            var items = await _repo.ListAssessmentsByPatientAsync(patientId, ownerUserId, isAdmin, ct);
            return Ok(new { items });
        }
    }
}
