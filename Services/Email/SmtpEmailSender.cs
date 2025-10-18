using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace EPApi.Services.Email
{
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly IConfiguration _cfg;

        public SmtpEmailSender(IConfiguration cfg)
        {
            _cfg = cfg;
        }

        public async Task SendAsync(string to, string subject, string htmlBody, string? textBody = null, CancellationToken ct = default)
        {
            var sec = _cfg.GetSection("Email");
            var fromName = sec["FromName"] ?? "No-Reply";
            var fromAddrCfg = sec["FromAddress"] ?? throw new InvalidOperationException("Email:FromAddress missing");

            var smtp = sec.GetSection("Smtp");
            var host = smtp["Host"] ?? throw new InvalidOperationException("Email:Smtp:Host missing");
            var port = int.TryParse(smtp["Port"], out var p) ? p : 587;
            var user = smtp["User"] ?? fromAddrCfg;
            var pass = smtp["Pass"] ?? throw new InvalidOperationException("Email:Smtp:Pass missing");
            var enableSsl = !string.Equals(smtp["EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);

            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            // 1) Normalizar cuerpos
            string html = htmlBody ?? "";
            // Quita doctype y <html>/<body> para mejorar compatibilidad con visores
            html = html.Replace("<!doctype html>", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("<html>", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("</html>", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("<body>", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("</body>", "", StringComparison.OrdinalIgnoreCase)
                       .Trim();

            string plain = textBody ?? System.Text.RegularExpressions.Regex
                .Replace(html, "<br>", "\n")
                .Replace("&nbsp;", " ")
                .Replace("<br/>", "\n")
                .Replace("<br />", "\n")
                .Replace("<p>", "\n\n")
                .Replace("</p>", "\n\n");

            plain = System.Text.RegularExpressions.Regex.Replace(plain, "<.*?>", string.Empty).Trim();

            using var msg = new MailMessage();
            msg.From = new MailAddress(user, fromName, System.Text.Encoding.UTF8);
            msg.To.Add(new MailAddress(to));
            msg.Subject = subject;
            msg.SubjectEncoding = System.Text.Encoding.UTF8;

            // 2) Multipart/alternative correcto: Plain como primera vista, HTML como alternativa
            //    (No ponemos el HTML en msg.Body para evitar duplicados en algunos visores)
            msg.Body = plain;
            msg.BodyEncoding = System.Text.Encoding.UTF8;
            msg.IsBodyHtml = false;
            msg.HeadersEncoding = System.Text.Encoding.UTF8;

            msg.AlternateViews.Clear();
            var plainView = AlternateView.CreateAlternateViewFromString(plain, System.Text.Encoding.UTF8, System.Net.Mime.MediaTypeNames.Text.Plain);
            var htmlView = AlternateView.CreateAlternateViewFromString(html, System.Text.Encoding.UTF8, System.Net.Mime.MediaTypeNames.Text.Html);
            msg.AlternateViews.Add(plainView);
            msg.AlternateViews.Add(htmlView);

            using var client = new SmtpClient(host, port)
            {
                DeliveryMethod = SmtpDeliveryMethod.Network,
                EnableSsl = enableSsl,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(user, pass),
                Timeout = 15000
            };

            ct.ThrowIfCancellationRequested();
            await client.SendMailAsync(msg);
        }
    }
}
