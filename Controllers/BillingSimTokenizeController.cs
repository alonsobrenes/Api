using System;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/billing/sim-tokenize")]
    public class BillingSimTokenizeController : ControllerBase
    {
        /// <summary>
        /// Pantalla simulada de TiloPay para tokenización de tarjeta.
        /// Muestra un botón "Autorizar" que redirige al endpoint /return con status=success & token=fake_...
        /// </summary>
        [HttpGet]
        public IActionResult Start([FromQuery] string returnUrl, [FromQuery] Guid org)
        {
            if (string.IsNullOrWhiteSpace(returnUrl))
            {
                return BadRequest(new { message = "returnUrl requerido" });
            }

            var tok = "fake_tok_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // HTML mínimo, similar al simulador de checkout.
            // Usamos links hacia /api/billing/sim-tokenize/return con los parámetros apropiados.
            var html = $@"
<!DOCTYPE html>
<html lang='es'>
<head>
  <meta charset='utf-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1' />
  <title>Sim Tokenize</title>
  <style>
    body {{ font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; padding: 24px; }}
    .wrap {{ max-width: 560px; margin: 0 auto; }}
    .box {{ border: 1px solid #ddd; border-radius: 8px; padding: 16px; }}
    .btn {{ display: inline-block; padding: 10px 14px; background: #2563eb; color: #fff; text-decoration: none; border-radius: 6px; }}
    .link {{ color: #2563eb; text-decoration: none; }}
    .muted {{ color: #666; font-size: 14px; }}
  </style>
</head>
<body>
  <div class='wrap'>
    <h2>Simulador de tokenización</h2>
    <div class='box'>
      <p class='muted'>Organización: {org}</p>
      <p>Este flujo simula la UI de TiloPay para tokenizar una tarjeta.</p>
      <p>
        <a class='btn' href='/api/billing/sim-tokenize/return?status=success&token={tok}&returnUrl={Uri.EscapeDataString(returnUrl)}'>Autorizar</a>
      </p>
      <p>
        <a class='link' href='/api/billing/sim-tokenize/return?status=cancel&returnUrl={Uri.EscapeDataString(returnUrl)}'>Cancelar</a>
      </p>
    </div>
  </div>
</body>
</html>";

            return new ContentResult
            {
                Content = html,
                ContentType = "text/html; charset=utf-8",
                StatusCode = 200
            };
        }

        /// <summary>
        /// Endpoint de retorno del simulador que redirige hacia el FE (/account/billing/pm-return)
        /// con los query params: status=(success|cancel) y, si aplica, token=fake_...
        /// </summary>
        [HttpGet("return")]
        public IActionResult Return([FromQuery] string status, [FromQuery] string returnUrl, [FromQuery] string? token = null)
        {
            if (string.IsNullOrWhiteSpace(returnUrl))
            {
                return BadRequest(new { message = "returnUrl requerido" });
            }

            var sep = returnUrl.Contains('?') ? "&" : "?";
            var redir = $"{returnUrl}{sep}status={Uri.EscapeDataString(status ?? string.Empty)}";

            if (!string.IsNullOrWhiteSpace(token))
            {
                redir += $"&token={Uri.EscapeDataString(token)}";
            }

            return Redirect(redir);
        }
    }
}
