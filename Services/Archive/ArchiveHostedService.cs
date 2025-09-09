using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EPApi.Services.Archive;
using EPApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EPApi.Services.Archive
{
    /// <summary>
    /// Job diario que ejecuta IArchiveService.RunOnceAsync a la hora programada.
    /// Usa IServiceScopeFactory para resolver IArchiveService (scoped) en cada corrida.
    /// </summary>
    public sealed class ArchiveHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly StorageArchiveOptions _opt;
        private readonly ILogger<ArchiveHostedService> _log;

        public ArchiveHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<StorageArchiveOptions> opt,
        ILogger<ArchiveHostedService> log)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _opt = opt?.Value ?? throw new ArgumentNullException(nameof(opt));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay = GetDelayUntilNextRun();
                _log.LogInformation("ArchiveHostedService: next run in {Delay}", delay);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var svc = scope.ServiceProvider.GetRequiredService<IArchiveService>();
                        var (ok, fail) = await svc.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                        _log.LogInformation("ArchiveHostedService: archived ok={Ok}, fail={Fail}", ok, fail);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ArchiveHostedService: unhandled error");
                }
            }
        }

        private TimeSpan GetDelayUntilNextRun()
        {
            if (TimeSpan.TryParseExact(_opt.DailyRunLocalTime, "hh\\:mm", CultureInfo.InvariantCulture, out var at))
            {
                var now = DateTime.Now;
                var next = new DateTime(now.Year, now.Month, now.Day, at.Hours, at.Minutes, 0);
                if (next <= now)
                {
                    next = next.AddDays(1);
                }
                return next - now;
            }

            // Por defecto: 24h
            return TimeSpan.FromHours(24);
        }
    }
}