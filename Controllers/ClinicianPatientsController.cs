// Controllers/ClinicianPatientsController.cs
using EPApi.DataAccess;
using EPApi.Services.Orgs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/clinician/patients")]
    [Authorize]
    public sealed class ClinicianPatientsController : ControllerBase
    {
        private readonly IClinicianReviewRepository _repo;
        private readonly IPatientRepository _patients;
        private readonly IOrgAccessService _orgAccess;

        public ClinicianPatientsController(
        IClinicianReviewRepository repo,
        IPatientRepository patients,
        IOrgAccessService orgAccess)
        {
            _repo = repo;
            _patients = patients;
            _orgAccess = orgAccess;
        }

        private int GetCurrentUserId()
        {
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")
                   ?? User.FindFirstValue("sub");
            return int.TryParse(raw, out var id) ? id : -1;
        }

        private Guid RequireOrgId()
        {
            if (!Request.Headers.TryGetValue("x-org-id", out var v) || !Guid.TryParse(v, out var orgId))
                throw new InvalidOperationException("x-org-id missing/invalid");
            return orgId;
        }

        // GET /api/clinician/patients/recent?take=5
        [HttpGet("recent")]
        public async Task<IActionResult> GetRecent([FromQuery] int take = 5, CancellationToken ct = default)
        {
            var orgId = RequireOrgId();
            var userId = GetCurrentUserId();

            var isOwner = await _orgAccess
            .IsOwnerOfMultiSeatOrgAsync(userId, orgId, ct);

            var items = await _repo.ListRecentPatientsAsync(userId, isOwner, take, ct);
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

            var orgId = RequireOrgId();
            var userId = GetCurrentUserId();

            var isOwner = await _orgAccess
            .IsOwnerOfMultiSeatOrgAsync(userId, orgId, ct);

            if (isOwner && orgId == null)
                return Forbid();

            var items = await _repo.ListAssessmentsByPatientAsync(
                patientId: patientId,
                viewerUserId: userId,
                isOwner: isOwner,
                orgId: orgId, 
                ct: ct
            );
            
            return Ok(new { items });
        }

        [HttpGet("by-clinician/{userId:int}")]
        public async Task<IActionResult> GetPatientsByClinician(int userId, CancellationToken ct = default)
        {
            var callerUserId = GetCurrentUserId();
            if (callerUserId == -1) return Forbid();

            var orgId = RequireOrgId();

            var isOwnerMulti = await _orgAccess.IsOwnerOfMultiSeatOrgAsync(callerUserId, orgId, ct);

            if (!isOwnerMulti) return Forbid();

            var items = await _patients.GetPatientsByClinicianAsync(orgId, userId, ct);
            return Ok(new { items });
        }
    }
}
