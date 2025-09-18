// Services/Search/ISearchService.cs
using EPApi.Models.Search;

namespace EPApi.Services.Search
{
    public interface ISearchService
    {
        Task<SearchResponseDto> SearchAsync(Guid orgId, SearchRequestDto req, CancellationToken ct);
        Task<SearchSuggestResponse> SuggestAsync(Guid orgId, string q, int limit, CancellationToken ct);
    }
}
