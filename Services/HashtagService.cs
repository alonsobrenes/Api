using System.Text;
using System.Text.RegularExpressions;
using EPApi.DataAccess;

namespace EPApi.Services
{
    public interface IHashtagService
    {
        /// <summary>
        /// Extrae hashtags explícitos (#palabra) del texto, los normaliza y
        /// sincroniza dbo.hashtags + dbo.hashtag_links para el target.
        /// </summary>
        Task<IReadOnlyList<string>> ExtractAndPersistAsync(
            Guid orgId, string targetType, Guid targetId, string? text, int maxTags = 5, CancellationToken ct = default);
    }

    public sealed class HashtagService : IHashtagService
    {
        private static readonly Regex RxHash = new(@"#([\p{L}\p{N}_-]{2,64})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly HashtagsRepository _repo;

        public HashtagService(HashtagsRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<string>> ExtractAndPersistAsync(
            Guid orgId, string targetType, Guid targetId, string? text, int maxTags = 5, CancellationToken ct = default)
        {            
            var tags = Extract(text);            
            if (tags.Count == 0)
            {
                // Si no hay hashtags explícitos, dejamos sin links (vacío)
                await _repo.ReplaceLinksAsync(orgId, targetType, targetId, Array.Empty<int>(), ct);
                return Array.Empty<string>();
            }

            // Limitar cantidad
            if (maxTags > 0 && tags.Count > maxTags)
                tags = tags.Take(maxTags).ToList();

            // Upsert y map a IDs
            var ids = new List<int>(tags.Count);
            foreach (var t in tags)
            {
                var id = await _repo.UpsertHashtagAsync(orgId, t, ct);
                ids.Add(id);
            }

            await _repo.ReplaceLinksAsync(orgId, targetType, targetId, ids, ct);
            return tags;
        }

        // ---------- helpers ----------
        private static List<string> Extract(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            foreach (Match m in RxHash.Matches(text))
            {
                var raw = m.Groups[1].Value; // sin '#'
                var norm = Normalize(raw);
                if (norm.Length < 2 || norm.Length > 64) continue;
                if (seen.Add(norm)) list.Add(norm);
            }
            return list;
        }

        // Normaliza: minusculas + quitar tildes (manteniendo ñ) + colapsar guiones bajos
        private static string Normalize(string s)
        {
            var lower = s.Trim().ToLowerInvariant();
            var deaccent = RemoveDiacriticsExceptEnye(lower);
            // colapsar dobles underscores o guiones
            deaccent = Regex.Replace(deaccent, @"[_-]{2,}", "_");
            return deaccent;
        }

        private static string RemoveDiacriticsExceptEnye(string s)
        {
            // Mantener ñ/Ñ; remover demás acentos
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
            {
                var uc = (int)ch;
                // ñ/Ñ
                if (uc == 241 || uc == 209) { sb.Append(char.ToLowerInvariant(ch)); continue; }
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
