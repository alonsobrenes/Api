using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EPApi.Services.Billing
{
    public sealed class DefaultPaymentMethodTokenizeContextProvider : IPaymentMethodTokenizeContextProvider
    {
        private readonly string _cs;

        public DefaultPaymentMethodTokenizeContextProvider(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("ConnectionString 'Default' requerido");
        }

        public async Task<PaymentMethodTokenizeContext> GetContextAsync(Guid orgId, CancellationToken ct)
        {
            // 1) Intentar leer org_billing_profiles (o la tabla que ya usas para BillingProfileForm)
            //    Asumo nombres típicos: email_contact, contact_first_name, contact_last_name, language, country_iso2
            string? email = null, firstName = null, lastName = null, lang = null, countryIso2 = null;
            int? ownerUserId = null;

            await using (var cn = new SqlConnection(_cs))
            {
                await cn.OpenAsync(ct);

                // owner
                const string sqlOwner = @"
SELECT TOP 1 om.user_id
FROM dbo.org_members om
WHERE om.org_id = @orgId AND om.role = 'owner'
ORDER BY om.user_id ASC;";
                await using (var cmd = new SqlCommand(sqlOwner, cn))
                {
                    cmd.Parameters.AddWithValue("@orgId", orgId);
                    var o = await cmd.ExecuteScalarAsync(ct);
                    if (o != null && o != DBNull.Value) ownerUserId = Convert.ToInt32(o);
                }

                // perfil de facturación (ajusta nombres de columnas si difieren)
                const string sqlProfile = @"
SELECT TOP 1 
    contact_email AS email_contact,
    CASE 
        WHEN CHARINDEX(' ', legal_name) > 0 
        THEN LEFT(legal_name, CHARINDEX(' ', legal_name) - 1)
        ELSE legal_name
    END AS contact_first_name,
    CASE 
        WHEN CHARINDEX(' ', legal_name) > 0 
        THEN SUBSTRING(legal_name, CHARINDEX(' ', legal_name) + 1, LEN(legal_name))
        ELSE ''
    END AS contact_last_name,
    '' language,
    bill_country_iso2
FROM dbo.org_billing_profiles
WHERE org_id = @orgId;";
                await using (var cmd = new SqlCommand(sqlProfile, cn))
                {
                    cmd.Parameters.AddWithValue("@orgId", orgId);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    if (await rd.ReadAsync(ct))
                    {
                        email = rd["email_contact"] as string;
                        firstName = rd["contact_first_name"] as string;
                        lastName = rd["contact_last_name"] as string;
                        lang = rd["language"] as string;
                        countryIso2 = rd["bill_country_iso2"] as string;
                    }
                }

                // 2) Si faltan datos, caer a users
                if (string.IsNullOrWhiteSpace(email) && ownerUserId.HasValue)
                {
                    const string sqlUser = @"SELECT email FROM dbo.users WHERE id = @uid;";
                    await using var cmd2 = new SqlCommand(sqlUser, cn);
                    cmd2.Parameters.AddWithValue("@uid", ownerUserId.Value);
                    var e = await cmd2.ExecuteScalarAsync(ct);
                    if (e != null && e != DBNull.Value) email = Convert.ToString(e);
                }

                if (string.IsNullOrWhiteSpace(firstName) && ownerUserId.HasValue)
                {
                    // Si tienes tabla de perfiles de usuario con nombres, úsala. Si no, derivamos del email
                    if (!string.IsNullOrWhiteSpace(email))
                        firstName = DeriveFirstNameFromEmail(email);
                    else
                        firstName = "Usuario";
                }

                if (string.IsNullOrWhiteSpace(lastName))
                {
                    lastName = "EP";
                }
            }

            // 3) Idioma: prioriza campo language, si no: por país; si no, "es"
            var language = NormalizeLanguage(lang, countryIso2);

            return new PaymentMethodTokenizeContext
            {
                Email = email ?? "no-reply@evaluacionpsicologica.org",
                FirstName = firstName!,
                LastName = lastName!,
                Language = language
            };
        }

        private static string NormalizeLanguage(string? lang, string? countryIso2)
        {
            if (!string.IsNullOrWhiteSpace(lang))
            {
                var l = lang.Trim().ToLowerInvariant();
                if (l == "es" || l == "en") return l;
            }
            if (!string.IsNullOrWhiteSpace(countryIso2))
            {
                var c = countryIso2.Trim().ToUpperInvariant();
                // LatAm y ES → es
                var esCountries = new HashSet<string> { "CR", "MX", "CO", "AR", "PE", "CL", "ES", "UY", "PY", "BO", "EC", "GT", "SV", "HN", "NI", "PA", "VE", "PR", "DO", "CU" };
                if (esCountries.Contains(c)) return "es";
            }
            return "es";
        }

        private static string DeriveFirstNameFromEmail(string email)
        {
            try
            {
                var left = email.Split('@')[0];
                if (left.Contains('.'))
                    left = left.Split('.')[0];
                return char.ToUpper(left[0]) + left.Substring(1);
            }
            catch { return "Usuario"; }
        }
    }
}
