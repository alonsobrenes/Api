namespace EPApi.DataAccess
{
    public interface IOrgRepository
    {
        Task<int> CountActiveMembersAsync(Guid orgId, CancellationToken ct);
        Task<string?> GetLogoUrlAsync(Guid orgId, CancellationToken ct);
        Task UpdateLogoUrlAsync(Guid orgId, string? logoUrl, CancellationToken ct);
    }
}
