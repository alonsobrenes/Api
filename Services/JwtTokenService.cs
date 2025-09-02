using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EPApi.Models;
using Microsoft.IdentityModel.Tokens;

namespace EPApi.Services
{
    public class JwtTokenService : IJwtTokenService
    {
        private readonly string _key;
        private readonly string _issuer;
        private readonly string _audience;
        private readonly TimeSpan _expiry;

        public JwtTokenService(string key, string issuer, string audience, TimeSpan expiry)
        {
            _key = key;
            _issuer = issuer;
            _audience = audience;
            _expiry = expiry;
        }

        public string GenerateToken(User user)
        {
            var role = (user.Role ?? "viewer").ToLowerInvariant();

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                expires: DateTime.UtcNow.Add(_expiry),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}