using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/patients/{patientId:guid}/consent")]
    [Authorize]
    public sealed class PatientConsentsController : ControllerBase
    {
        private readonly IPatientConsentsRepository _repo;

        // Por ahora, un solo tipo de consentimiento: el universal general de psicoterapia
        private const string ConsentTypePsychotherapyGeneral = "psychotherapy_general";
        private const string ConsentVersionUniversalV1 = "universal_v1_2024-11";
        private readonly IOrgBillingProfileRepository _orgProfileRepo;

        public PatientConsentsController(IPatientConsentsRepository repo, IOrgBillingProfileRepository orgProfileRepo)
        {
            _repo = repo;
            _orgProfileRepo = orgProfileRepo;
        }

        /// <summary>
        /// Devuelve el último consentimiento firmado para este paciente
        /// (o null si aún no tiene ninguno).
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<PatientConsentDto?>> GetLatest(Guid patientId, CancellationToken ct)
        {
            var dto = await _repo.GetLatestAsync(patientId, ConsentTypePsychotherapyGeneral, ct);
            return Ok(dto);
        }

        /// <summary>
        /// Historial de consentimientos para este paciente (mismo tipo).
        /// Útil a futuro si quieres ver versiones o revocaciones.
        /// </summary>
        [HttpGet("history")]
        public async Task<ActionResult<PatientConsentDto[]>> GetHistory(Guid patientId, CancellationToken ct)
        {
            var list = await _repo.GetHistoryAsync(patientId, ConsentTypePsychotherapyGeneral, ct);
            return Ok(list);
        }

        public sealed record CreateConsentRequest(
            string SignedName,
            string? SignedIdNumber,
            string SignedByRelationship,
            string? CountryCode,
            string? Language,
            string? SignatureUri,
            string? RawConsentText,
            string? LocalAddendumCountry,
            string? LocalAddendumVersion
        );

        private Guid GetOrgIdOrThrow()
        {
            var claim = User.FindFirst("org_id")?.Value;
            if (Guid.TryParse(claim, out var gid)) return gid;

            var header = Request.Headers["X-Org-Id"].FirstOrDefault();
            if (Guid.TryParse(header, out gid)) return gid;

            throw new InvalidOperationException("Missing org_id (claim or X-Org-Id header).");
        }

        /// <summary>
        /// Registra un nuevo consentimiento firmado para el paciente.
        /// La versión del texto (universal y addendum) está fijada por constantes,
        /// y el snapshot del texto mostrado puede venir en RawConsentText.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Create(Guid patientId, [FromBody] CreateConsentRequest body, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            var userId = RequireUserId();
            var orgId = GetOrgIdOrThrow();

            var billingProfile = await _orgProfileRepo.GetAsync(orgId, ct);

            if (billingProfile == null)
            {
                return Problem(
                    detail: "No se encontró un perfil de facturación para la organización.",
                    statusCode: 500);
            }

            var countryCode = billingProfile.BillingAddress.CountryIso2.Trim().ToUpperInvariant();
            var language = LocalizationUtils.NormalizeLanguage(null, countryCode);

            var id = await _repo.CreateAsync(
                patientId: patientId,
                createdByUserId: userId,
                consentType: ConsentTypePsychotherapyGeneral,
                consentVersion: ConsentVersionUniversalV1,
                localAddendumCountry: body.LocalAddendumCountry,
                localAddendumVersion: body.LocalAddendumVersion,
                countryCode: countryCode,
                language: language,
                signedName: body.SignedName.Trim(),
                signedIdNumber: string.IsNullOrWhiteSpace(body.SignedIdNumber) ? null : body.SignedIdNumber.Trim(),
                signedByRelationship: string.IsNullOrWhiteSpace(body.SignedByRelationship) ? "paciente" : body.SignedByRelationship.Trim(),
                signatureUri: body.SignatureUri,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers["User-Agent"].ToString(),
                rawConsentText: body.RawConsentText,
                ct: ct
            );

            var dto = await _repo.GetLatestAsync(patientId, ConsentTypePsychotherapyGeneral, ct);

            if (dto is null)
            {
                // Caso extremadamente raro: se creó pero no se puede leer
                return Problem(
                    detail: "El consentimiento fue creado pero no se pudo cargar el registro.",
                    statusCode: 500
                );
            }

            // Devolvemos el DTO completo
            return CreatedAtAction(nameof(GetLatest), new { patientId }, dto);
        }

        // --------------------------------------------------------------------
        // Helpers (copiados del patrón de otros controllers)

        private int RequireUserId()
        {
            var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
            if (!int.TryParse(idStr, out var uid))
                throw new UnauthorizedAccessException("No user id");
            return uid;
        }
    }
}
