using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public sealed class AuthController : ControllerBase
    {
        private readonly IRegistrationService _registration;
        private readonly ILogger<AuthController> _logger;
        private readonly IConfiguration _cfg;
        private readonly IUserRepository _users;
        private readonly IPasswordHasher _hasher;
        private readonly IAuthService _auth;

        public AuthController(
            IAuthService auth,
            IRegistrationService registration,
            ILogger<AuthController> logger,
            IConfiguration cfg,
            IUserRepository users,
            IPasswordHasher hasher)
        {
            _registration = registration;
            _logger = logger;
            _cfg = cfg;
            _users = users;
            _hasher = hasher;
            _auth = auth;
        }

        // Nota: mantengo el DTO de registro existente, y lo extiendo con planCode opcional.
        public sealed class RegisterRequest
        {
            public string Email { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string? Role { get; set; }
            public string? PlanCode { get; set; }   // NUEVO: plan inicial para trial (opcional)
        }

        public sealed class RegisterResponse
        {
            public int UserId { get; set; }
            public Guid OrgId { get; set; }
            public string Message { get; set; } = "OK";
        }

        public record LoginDto(string Email, string Password);

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken ct)
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
                return BadRequest(new { message = "Email and Password are required." });

            var token = await _auth.LoginAsync(dto.Email, dto.Password, ct);
            if (token is null) return Unauthorized(new { message = "Invalid credentials." });

            return Ok(new { token });
        }



        [AllowAnonymous]
        [HttpPost("signup")]
        public async Task<ActionResult<RegisterResponse>> Signup([FromBody] RegisterRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (req is null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            {
                return BadRequest(new { message = "Email y Password requeridos" });
            }

            req.Email = req.Email.Trim();

            if (await _users.ExistsByEmailAsync(req.Email, ct))
                return Conflict(new { message = "Email already exists." });

            var user = new User
            {
                Email = req.Email,
                PasswordHash = _hasher.Hash(req.Password),
                Role = string.IsNullOrWhiteSpace(req.Role) ? "editor" : req.Role.Trim()
            };

            var id = await _users.CreateAsync(user, ct);


            var orgId = await _registration.RegisterAsync(req, id, ct);

            return CreatedAtAction(nameof(Me), new { id }, new { id, email = user.Email, role = user.Role, orgId });
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
        {
            var userName = User.Identity?.Name ?? "(unknown)";
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(unknown)";
            var role = User.FindFirstValue(ClaimTypes.Role) ?? "User";
            return Ok(new { userName, userId, role });
        }
    }
}
