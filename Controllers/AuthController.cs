using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EPApi.Services;
using EPApi.DataAccess;
using EPApi.Models;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]    
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _auth;
        private readonly IUserRepository _users;
        private readonly IPasswordHasher _hasher;
        private readonly IRegistrationService _registration;

        public AuthController(IAuthService auth, IUserRepository users, IPasswordHasher hasher, IRegistrationService registration)
        {
            _auth = auth;
            _users = users;
            _hasher = hasher;
            _registration = registration;
        }

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

        [HttpPost("signup")]
        [AllowAnonymous]
        public async Task<IActionResult> Signup([FromBody] SignupDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            dto.Email = dto.Email.Trim();
            if (await _users.ExistsByEmailAsync(dto.Email, ct))
                return Conflict(new { message = "Email already exists." });

            var user = new User
            {
                Email = dto.Email,
                PasswordHash = _hasher.Hash(dto.Password),
                Role = string.IsNullOrWhiteSpace(dto.Role) ? "editor" : dto.Role.Trim()
            };

            var id = await _users.CreateAsync(user, ct);

            // Crea org + membresía + trial
            var orgId = await _registration.CreateOrgAndMembershipAndTrialAsync(id, orgName: user.Email, ct);


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

    public record LoginDto(string Email, string Password);

    public class SignupDto
    {
        [Required, MinLength(3), MaxLength(100)]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(6), MaxLength(128)]
        public string Password { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? Role { get; set; }
    }
}