using EPApi.DataAccess;
using EPApi.Services.Email;
using EPApi.Services.Orgs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using System.Data;
using System.Net.Mail;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/orgs")]
    public sealed class OrgAdminController : ControllerBase
    {
        private readonly BillingRepository _billingRepo;
        private readonly IConfiguration _cfg;
        private readonly IHostEnvironment _env;
        private readonly IEmailSender _email;
        private readonly IOrgRepository _orgRepository;
        private readonly IClinicianReviewRepository _reviewRepo;
        private readonly IOrgAccessService _orgAccess;

        public OrgAdminController(BillingRepository billingRepo, IConfiguration cfg, IHostEnvironment env, IEmailSender email, IOrgRepository orgRepository, IClinicianReviewRepository reviewRepo, IOrgAccessService orgAccess)
        {
            _billingRepo = billingRepo;
            _cfg = cfg;
            _env = env;
            _email = email;
            _orgRepository = orgRepository;
            _reviewRepo = reviewRepo;
            _orgAccess = orgAccess;
        }

        public sealed record OrgSummaryDto(string planCode, string status, int seats, string kind);

        private bool TryGetUserId(out int uid)
        {
            uid = 0;
            var c =
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ??
                User.FindFirst("sub")?.Value ??
                User.FindFirst("nameid")?.Value;

            return int.TryParse(c, out uid);
        }

        private async Task<(string html, string text)> RenderInvitationAsync(string acceptUrl, DateTime? expiresAtUtc, string? orgDisplay, CancellationToken ct)
        {
            var root = _env.ContentRootPath;
            var htmlPath = Path.Combine(root, "Templates", "Email", "Invitation.html");
            var txtPath = Path.Combine(root, "Templates", "Email", "Invitation.txt");

            string html = await System.IO.File.ReadAllTextAsync(htmlPath, ct);
            string txt = await System.IO.File.ReadAllTextAsync(txtPath, ct);

            var expires = expiresAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "N/A";
            var rep = new Dictionary<string, string>
            {
                ["{{AcceptUrl}}"] = acceptUrl,
                ["{{ExpiresAt}}"] = expires,
                ["{{OrgDisplay}}"] = string.IsNullOrWhiteSpace(orgDisplay) ? "tu organización" : orgDisplay!
            };

            foreach (var (k, v) in rep)
            {
                html = html.Replace(k, v);
                txt = txt.Replace(k, v);
            }
            return (html, txt);
        }

        private async Task<(bool ok, Guid orgId)> TryGetOrgIdAsync(CancellationToken ct)
        {
            if (TryGetUserId(out var uid))
            {
                var org = await _billingRepo.GetOrgIdForUserAsync(uid, ct);
                if (org is not null) return (true, org.Value);
            }

            // Dev fallback (igual que en BillingController)
            if (_env.IsDevelopment())
            {
                var dev = _cfg["Billing:DevOrgId"];
                if (Guid.TryParse(dev, out var g)) return (true, g);
            }

            return (false, Guid.Empty);
        }

        [HttpGet("current/summary")]
        [Authorize]
        public async Task<ActionResult<OrgSummaryDto>> GetCurrentOrgSummary(CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok)
                return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });

            var summary = await _billingRepo.GetOrgPlanSummaryAsync(orgTry.orgId, ct);
            if (summary is null)
            {
                // Sin suscripción: seats = 1 por defecto (solo) para no romper el FE
                return Ok(new OrgSummaryDto("solo", "none", 1, "solo"));
            }

            var (planCode, status, seats) = summary.Value;

            // Derivar “kind” solo para la UI (sin persistir)
            var kind = seats <= 1 ? "solo" : (seats <= 9 ? "clinic" : "hospital");

            return Ok(new OrgSummaryDto(planCode, status, seats, kind));
        }

        // GET /api/orgs/patients-by-professional?from=...&to=...
        [HttpGet("patients-by-professional")]
        public async Task<IActionResult> GetPatientsByProfessional(
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            CancellationToken ct = default)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok)
                return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });

            TryGetUserId(out var userId);

            // Solo owner de clínica / hospital (multi-seat)
            var isOwner = await _orgAccess.IsOwnerOfMultiSeatOrgAsync(userId, orgTry.orgId, ct);
            if (!isOwner)
            {
                return Forbid();
            }

            // Normalizamos a UTC por si el controlador recibe DateTime Kind=Unspecified
            var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
            var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);

            var list = await _reviewRepo.GetOrgPatientsByProfessionalAsync(
                orgTry.orgId,
                fromUtc,
                toUtc,
                ct);

            return Ok(list);
        }


        public sealed class MemberDto
        {
            public int userId { get; set; }
            public string email { get; set; } = "";
            public string? userRole { get; set; }
            public string? memberRole { get; set; }
            public DateTime createdAtUtc { get; set; }
            public string? avatarUrl { get; set; }
            public string? firstName { get; set; }
            public string? lastName1 { get; set; }
            public string? lastName2 { get; set; }
            public string? phone { get; set; }
            public string? titlePrefix { get; set; }
            public string? licenseNumber { get; set; }
            public string? signatureImageUrl { get; set; }
        }

        [HttpGet("members")]
        [Authorize]
        public async Task<ActionResult<IReadOnlyList<MemberDto>>> GetMembersAsync(CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok)
                return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });

            var list = new List<MemberDto>();
            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración",
                    detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            const string sql = @"
SELECT m.user_id,
       u.email,
       u.role       AS user_role,
       m.role       AS member_role,
       m.created_at_utc,
       u.avatar_url,
       u.first_name,
       u.last_name1,
       u.last_name2,
       u.phone,
       u.title_prefix,
       u.license_number,
       u.signature_image_url
FROM dbo.org_members m
JOIN dbo.users u ON u.id = m.user_id
WHERE m.org_id = @org
ORDER BY u.email;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgTry.orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new MemberDto
                {
                    userId = rd.GetInt32(0),
                    email = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    userRole = rd.IsDBNull(2) ? null : rd.GetString(2),
                    memberRole = rd.IsDBNull(3) ? null : rd.GetString(3),
                    createdAtUtc = rd.GetDateTime(4),
                    avatarUrl = rd.IsDBNull(5) ? "" : rd.GetString(5),
                    firstName = rd.IsDBNull(6) ? null : rd.GetString(6),
                    lastName1 = rd.IsDBNull(7) ? null : rd.GetString(7),
                    lastName2 = rd.IsDBNull(8) ? null : rd.GetString(8),
                    phone = rd.IsDBNull(9) ? null : rd.GetString(9),
                    titlePrefix = rd.IsDBNull(10) ? null : rd.GetString(10),
                    licenseNumber = rd.IsDBNull(11) ? null : rd.GetString(11),
                    signatureImageUrl = rd.IsDBNull(12) ? null : rd.GetString(12),
                });
            }

            return Ok(list);
        }

        // ===== DTOs para invitaciones =====
        public sealed class InvitationDto
        {
            public Guid id { get; set; }
            public string email { get; set; } = "";
            public string? role { get; set; }
            public string status { get; set; } = "pending";
            public DateTime createdAtUtc { get; set; }
            public DateTime? expiresAtUtc { get; set; }
            public DateTime? acceptedAtUtc { get; set; }
            public DateTime? revokedAtUtc { get; set; }
        }

        public sealed class CreateInvitationDto
        {
            public string email { get; set; } = "";
            public string? role { get; set; } // opcional
            public int? expiresDays { get; set; } // opcional (por defecto 14)
        }

        // ===== GET /api/orgs/invitations =====
        // Lista invitaciones de la organización actual (por defecto pending)
        [HttpGet("invitations")]
        [Authorize]
        public async Task<ActionResult<IReadOnlyList<InvitationDto>>> GetInvitationsAsync([FromQuery] string? status, CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok)
                return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });

            var list = new List<InvitationDto>();
            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración",
                    detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            var filter = string.IsNullOrWhiteSpace(status) ? "pending" : status!.Trim().ToLowerInvariant();

            const string sql = @"
SELECT id, email, role, status, created_at_utc, expires_at_utc, accepted_at_utc, revoked_at_utc
FROM dbo.org_invitations
WHERE org_id = @org AND status = @st
ORDER BY created_at_utc DESC;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgTry.orgId });
            cmd.Parameters.Add(new SqlParameter("@st", SqlDbType.NVarChar, 20) { Value = filter });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new InvitationDto
                {
                    id = rd.GetGuid(0),
                    email = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    role = rd.IsDBNull(2) ? null : rd.GetString(2),
                    status = rd.IsDBNull(3) ? "pending" : rd.GetString(3),
                    createdAtUtc = rd.GetDateTime(4),
                    expiresAtUtc = rd.IsDBNull(5) ? null : rd.GetDateTime(5),
                    acceptedAtUtc = rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                    revokedAtUtc = rd.IsDBNull(7) ? null : rd.GetDateTime(7),
                });
            }

            return Ok(list);
        }

        // ===== POST /api/orgs/invitations =====
        // Crea una invitación si hay seats disponibles
        [HttpPost("invitations")]
        [Authorize]
        public async Task<ActionResult<InvitationDto>> CreateInvitationAsync([FromBody] CreateInvitationDto dto, CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok)
                return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });

            // Validación email (fuerte)
            if (string.IsNullOrWhiteSpace(dto.email))
                return ValidationProblem("Email es obligatorio.");
            try { var _ = new MailAddress(dto.email.Trim()); }
            catch { return ValidationProblem("Email inválido."); }

            // Seats
            var summary = await _billingRepo.GetOrgPlanSummaryAsync(orgTry.orgId, ct);
            var seats = summary?.seats ?? 0;
            if (seats <= 0)
                return Problem(statusCode: 402, title: "Límite del plan",
                    detail: "Tu plan no permite agregar profesionales.");

            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración",
                    detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            // Miembros actuales
            int membersCount;
            const string sqlCount = @"SELECT COUNT(*) FROM dbo.org_members WHERE org_id = @org;";
            await using (var cmdCount = new SqlCommand(sqlCount, cn))
            {
                cmdCount.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgTry.orgId });
                membersCount = (int)await cmdCount.ExecuteScalarAsync(ct);
            }
            if (membersCount >= seats)
                return Problem(statusCode: 402, title: "Límite del plan",
                    detail: $"Has alcanzado el máximo de {seats} profesionales para tu plan.");

            var expiresDays = (dto.expiresDays.HasValue && dto.expiresDays.Value > 0) ? dto.expiresDays.Value : 5;
            var expiresAt = (DateTime?)DateTime.UtcNow.AddDays(expiresDays);
            var token = GenerateSecureTokenUrlSafe(48);
            var id = Guid.NewGuid();
            var fixedRole = "editor";

            const string sqlIns = @"
INSERT INTO dbo.org_invitations (id, org_id, email, role, token, status, created_at_utc, expires_at_utc, invited_by_user_id)
VALUES (@id, @org, @em, @role, @token, N'pending', SYSUTCDATETIME(), @exp, @by);";

            await using (var cmdIns = new SqlCommand(sqlIns, cn))
            {
                cmdIns.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmdIns.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgTry.orgId });
                cmdIns.Parameters.Add(new SqlParameter("@em", SqlDbType.NVarChar, 256) { Value = dto.email.Trim().ToLowerInvariant() });
                cmdIns.Parameters.Add(new SqlParameter("@role", SqlDbType.NVarChar, 30) { Value = (object?)fixedRole ?? DBNull.Value });
                cmdIns.Parameters.Add(new SqlParameter("@token", SqlDbType.NVarChar, 80) { Value = token });
                cmdIns.Parameters.Add(new SqlParameter("@exp", SqlDbType.DateTime2) { Value = (object?)expiresAt ?? DBNull.Value });
                cmdIns.Parameters.Add(new SqlParameter("@by", SqlDbType.Int) { Value = TryGetUserId(out var uid) ? uid : 0 });
                await cmdIns.ExecuteNonQueryAsync(ct);
            }

            // Construir AcceptUrl con configuración
            var e = _cfg.GetSection("Email");
            var baseUrl = e["BaseUrl"]?.TrimEnd('/') ?? "http://localhost:5173";
            var path = e["InviteAcceptPath"] ?? "/invite/accept";
            var acceptUrl = $"{baseUrl}{(path.StartsWith("/") ? "" : "/")}{path}?token={Uri.EscapeDataString(token)}";

            // Render template y enviar
            var orgDisplay = $"{summary?.planCode ?? "plan"} ({seats} asientos)"; // simple, editable luego
            var (html, txt) = await RenderInvitationAsync(acceptUrl, expiresAt, orgDisplay, ct);
            await _email.SendAsync(dto.email.Trim(), "Invitación a unirte a la organización", html, txt, ct);

            var res = new InvitationDto
            {
                id = id,
                email = dto.email.Trim().ToLowerInvariant(),
                role = fixedRole,
                status = "pending",
                createdAtUtc = DateTime.UtcNow,
                expiresAtUtc = expiresAt,
            };

            return Ok(res);
        }

        public sealed class InvitationPrecheckDto
        {
            public bool valid { get; set; }
            public string email { get; set; } = "";
            public DateTime? expiresAtUtc { get; set; }
            public string status { get; set; } = "unknown";
        }

        [HttpPost("invitations/{token}/precheck")]
        [AllowAnonymous]
        public async Task<ActionResult<InvitationPrecheckDto>> PrecheckInvitationAsync([FromRoute] string token, CancellationToken ct)
        {
            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración",
                    detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            const string sql = @"
SELECT email, status, expires_at_utc
FROM dbo.org_invitations
WHERE token = @t;";

            string? email = null;
            string status = "unknown";
            DateTime? exp = null;

            await using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 80) { Value = token });
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    email = rd.IsDBNull(0) ? null : rd.GetString(0);
                    status = rd.IsDBNull(1) ? "unknown" : rd.GetString(1);
                    exp = rd.IsDBNull(2) ? null : rd.GetDateTime(2);
                }
            }

            var ok = !string.IsNullOrWhiteSpace(email)
                     && string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
                     && (exp is null || exp.Value > DateTime.UtcNow);

            return Ok(new InvitationPrecheckDto
            {
                valid = ok,
                email = email ?? "",
                status = status,
                expiresAtUtc = exp
            });
        }

        // ===== DELETE /api/orgs/invitations/{id} =====
        // Revoca una invitación pendiente
        [HttpDelete("invitations/{id:guid}")]
        [Authorize]
        public async Task<IActionResult> RevokeInvitationAsync([FromRoute] Guid id, CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok)
                return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });

            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración",
                    detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            const string sql = @"
UPDATE dbo.org_invitations
SET status = N'revoked', revoked_at_utc = SYSUTCDATETIME()
WHERE id = @id AND org_id = @org AND status = N'pending';";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgTry.orgId });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) return NotFound(new { message = "Invitación no encontrada o ya no está pendiente." });

            return NoContent();
        }

        // ===== POST /api/orgs/invitations/{token}/accept =====
        // Acepta la invitación (requiere usuario autenticado); crea org_member si hay seats disponibles.
        [HttpPost("invitations/{token}/accept")]
        [Authorize]
        public async Task<IActionResult> AcceptInvitationAsync([FromRoute] string token, CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!TryGetUserId(out var uid) || uid <= 0)
                return Unauthorized(new { message = "Usuario no autenticado." });

            // Email del usuario autenticado (del JWT)
            string? authEmail =
                User?.Claims?.FirstOrDefault(c =>
                    c.Type == System.Security.Claims.ClaimTypes.Email ||
                    c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")
                ?.Value;

            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración",
                    detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);

            // 1) Cargar invitación pendiente por token (y no expirada)
            const string sqlSel = @"
SELECT id, org_id, email, role, status, expires_at_utc
FROM dbo.org_invitations
WHERE token = @t AND status = N'pending';";

            Guid invId; Guid orgId; string invitedEmail; string? role; DateTime? expiresAt;

            await using (var cmdSel = new SqlCommand(sqlSel, cn, (SqlTransaction)tx))
            {
                cmdSel.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 80) { Value = token });
                await using var rd = await cmdSel.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return NotFound(new { message = "Invitación no válida o ya utilizada." });
                }
                invId = rd.GetGuid(0);
                orgId = rd.GetGuid(1);
                invitedEmail = rd.IsDBNull(2) ? "" : rd.GetString(2);
                role = rd.IsDBNull(3) ? null : rd.GetString(3);
                var status = rd.IsDBNull(4) ? "pending" : rd.GetString(4);
                expiresAt = rd.IsDBNull(5) ? null : rd.GetDateTime(5);

                if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(ct);
                    return Problem(statusCode: 409, title: "Invitación no disponible",
                        detail: "La invitación no está pendiente.");
                }
                if (expiresAt is DateTime dt && dt < DateTime.UtcNow)
                {
                    await tx.RollbackAsync(ct);
                    return Problem(statusCode: 409, title: "Invitación expirada",
                        detail: "Solicita una nueva invitación.");
                }
            }

            // 1.b) EXIGIR COINCIDENCIA DE EMAIL
            // Si el token pertenece a invitedEmail, el autenticado debe tener ese mismo correo
            if (string.IsNullOrWhiteSpace(authEmail) ||
                !string.Equals(authEmail.Trim(), invitedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(ct);
                return Forbid(); // 403: el email autenticado no coincide con la invitación
            }

            // 2) Verificar seats disponibles
            var summary = await _billingRepo.GetOrgPlanSummaryAsync(orgId, ct);
            var seats = summary?.seats ?? 0;

            int membersCount;
            const string sqlCount = @"SELECT COUNT(*) FROM dbo.org_members WHERE org_id = @org;";
            await using (var cmdCount = new SqlCommand(sqlCount, cn, (SqlTransaction)tx))
            {
                cmdCount.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                membersCount = (int)await cmdCount.ExecuteScalarAsync(ct);
            }

            if (membersCount >= seats)
            {
                await tx.RollbackAsync(ct);
                return Problem(statusCode: 402, title: "Límite del plan",
                    detail: $"Has alcanzado el máximo de {seats} profesionales para tu plan.");
            }

            // 3) Insertar miembro (idempotente por PK org_id+user_id)
            const string sqlInsMember = @"
IF NOT EXISTS (SELECT 1 FROM dbo.org_members WHERE org_id = @org AND user_id = @uid)
BEGIN
    INSERT INTO dbo.org_members (org_id, user_id, role)
    VALUES (@org, @uid, @role);
END";
            await using (var cmdInsM = new SqlCommand(sqlInsMember, cn, (SqlTransaction)tx))
            {
                cmdInsM.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmdInsM.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = uid });
                cmdInsM.Parameters.Add(new SqlParameter("@role", SqlDbType.NVarChar, 30) { Value = (object?)role ?? DBNull.Value });
                await cmdInsM.ExecuteNonQueryAsync(ct);
            }

            // 4) Marcar invitación como aceptada
            const string sqlUpdInv = @"
UPDATE dbo.org_invitations
SET status = N'accepted', accepted_user_id = @uid, accepted_at_utc = SYSUTCDATETIME()
WHERE id = @id;";
            await using (var cmdUpdInv = new SqlCommand(sqlUpdInv, cn, (SqlTransaction)tx))
            {
                cmdUpdInv.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = uid });
                cmdUpdInv.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = invId });
                await cmdUpdInv.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return NoContent();
        }

        // DELETE /api/orgs/members/{userId}
        [HttpDelete("members/{userId:int}")]
        [Authorize]
        public async Task<IActionResult> RemoveMemberAsync([FromRoute] int userId, CancellationToken ct)
        {
            // Org actual (owner/adm autorizado por tu pipeline)
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized();

            // Evitar que el usuario se elimine a sí mismo por accidente (opcional)
            if (TryGetUserId(out var callerId) && callerId == userId)
                return Problem(statusCode: 400, title: "Operación no permitida", detail: "No puedes eliminar tu propio usuario.");

            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs))
                return Problem(statusCode: 500, title: "Configuración", detail: "ConnectionStrings:Default no configurada.");

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            const string sql = @"
DELETE FROM dbo.org_members
WHERE org_id = @org AND user_id = @uid;";

            int aff;
            await using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgTry.orgId });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                aff = await cmd.ExecuteNonQueryAsync(ct);
            }

            if (aff == 0) return NotFound();
            return NoContent();
        }


        // ===== Helper para token seguro base64url =====
        private static string GenerateSecureTokenUrlSafe(int bytesLen)
        {
            var bytes = new byte[bytesLen];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            var b64 = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return b64;
        }

    }
}
