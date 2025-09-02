using System.Security.Cryptography;
using System.Text;

namespace EPApi.Services
{
    public static class Crypto
    {
        public static string Sha256Hex(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
