// Services/Billing/ITiloPayAuthTokenProvider.cs
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services.Billing
{
    public interface ITiloPayAuthTokenProvider
    {
        Task<string> GetBearerAsync(CancellationToken ct);
    }
}
