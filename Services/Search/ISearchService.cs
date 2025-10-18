// Services/Search/ISearchService.cs
using EPApi.Models.Search;

namespace EPApi.Services.Search
{
    public interface ISearchService
    {
        //Task<SearchResponseDto> SearchAsync(Guid orgId, SearchRequestDto req, CancellationToken ct);
        //Task<SearchSuggestResponse> SuggestAsync(Guid orgId, string q, int limit, CancellationToken ct);

        // después:
        Task<SearchResponseDto> SearchAsync(Guid orgId, SearchRequestDto req, bool allowProfessionals, CancellationToken ct = default);
        Task<SearchSuggestResponse> SuggestAsync(Guid orgId, string q, int limit, bool allowProfessionals, CancellationToken ct = default);

    }
}
