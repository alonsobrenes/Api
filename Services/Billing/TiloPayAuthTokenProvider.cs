// Services/Billing/TiloPayAuthTokenProvider.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EPApi.Services.Billing
{
    public sealed class TiloPayAuthTokenProvider : ITiloPayAuthTokenProvider
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private const string CacheKey = "tilopay:access_token";
        private readonly IMemoryCache _cache;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<TiloPayAuthTokenProvider> _log;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public TiloPayAuthTokenProvider(
            IMemoryCache cache,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            ILogger<TiloPayAuthTokenProvider> log)
        {
            _cache = cache;
            _httpFactory = httpFactory;
            _cfg = cfg;
            _log = log;
        }

        public async Task<string> GetBearerAsync(CancellationToken ct)
        {
            if (_cache.TryGetValue<string>(CacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;

            await _gate.WaitAsync(ct);
            try
            {
                // doble verificación tras adquirir el candado
                if (_cache.TryGetValue<string>(CacheKey, out cached) && !string.IsNullOrWhiteSpace(cached))
                    return cached;

                var baseUrl = GetBaseUrl();
                var user = GetApiUser();
                var pass = GetApiPass();
                var apiKey = GetApiKey();

                var client = _httpFactory.CreateClient("TiloPay.SafeClient");
                var body = new { apiuser = user, password = pass, key = apiKey };

                var json = JsonSerializer.Serialize(body, JsonOpts);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/login");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var res = await client.SendAsync(req, ct);
                var str = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                    throw new InvalidOperationException($"TiloPay login falló: {res.StatusCode} {str}");

                using var doc = JsonDocument.Parse(str);
                var token = doc.RootElement.GetProperty("access_token").GetString();
                
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("TiloPay token missing in response");

                // TTL: si TiloPay no informa expiración, cachear p.ej. 25 minutos y renovar antes
                var ttlMinutes = _cfg.GetValue<int?>("TiloPay:TokenTtlMinutes") ?? 25;
                _cache.Set(CacheKey, token!, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ttlMinutes)
                });

                return token!;
            }
            finally
            {
                _gate.Release();
            }
        }

        // ====== Config helpers ======

        private string GetBaseUrl()
        {
            var v = _cfg["TiloPay:BaseUrl"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:BaseUrl requerido");
            return v.TrimEnd('/');
        }
        private string GetApiUser()
        {
            var v = _cfg["TiloPay:ApiUser"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:ApiUser requerido");
            return v;
        }
        private string GetApiPass()
        {
            var v = _cfg["TiloPay:ApiPass"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:ApiPass requerido");
            return v;
        }
        private string GetApiKey()
        {
            var v = _cfg["TiloPay:ApiKey"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:ApiKey requerido");
            return v;
        }
        private string GetReturnUrlBase()
        {
            var v = _cfg["Billing:ReturnUrlBase"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("Billing:ReturnUrlBase requerido");
            return v;
        }
    }
}
