namespace EPApi.Services.Billing
{
    public interface ITrialProvisioner
    {
        Task EnsureTrialAsync(Guid orgId, CancellationToken ct);
    }
}
