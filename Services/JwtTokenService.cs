using EPApi.Models;
using EPApi.Services.Orgs;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

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

        public string GenerateToken(User user, Guid? orgId = null, bool? isOwner = false)
        {
            var role = (bool)isOwner ? user.Role.ToLowerInvariant() : "viewer";

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            if (orgId!=null)
                claims.Add(new Claim("org_id", orgId.ToString()));
          
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