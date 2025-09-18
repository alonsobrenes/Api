// Models/SearchDtos.cs
using EPApi.Services.Search;
using System.ComponentModel.DataAnnotations;

namespace EPApi.Models.Search
{
    public sealed class SearchRequestDto
    {
        // Texto libre (puede venir vacío si se filtra solo por labels/hashtags/fechas)
        public string? Q { get; set; }

        // Tipos a buscar (MVP): "patient", "interview", "session", "test", "attachment"
        public string[]? Types { get; set; }

        // Filtros (opcionales)
        public Guid[]? Labels { get; set; }        // ids de label (GUID)
        public string[]? Hashtags { get; set; }    // tags normalizados (sin '#')

        public DateTime? DateFromUtc { get; set; } // filtra por updatedAt/createdAt (según tipo)
        public DateTime? DateToUtc { get; set; }

        // Paginación
        [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
        [Range(1, 200)] public int PageSize { get; set; } = 20;
    }

    public sealed class SearchResultItemDto
    {
        public string Type { get; set; } = "";     // patient|interview|session|test|attachment
        public string Id { get; set; } = "";       // usar string para no amarrarnos a GUID/INT
        public string? Title { get; set; }
        public string? Snippet { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }

        public List<LabelChipDto> Labels { get; set; } = new();
        public List<string> Hashtags { get; set; } = new();
        public string? Url { get; set; }           // deep-link de UI (opcional)
    }

    public sealed class LabelChipDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#999999";
    }

    public sealed class SearchResponseDto
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public long Total { get; set; }
        public List<SearchResultItemDto> Items { get; set; } = new();
        public long DurationMs { get; set; }       // logging básico de performance
    }

    public sealed class LabelSuggestDto
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#999999";
    }

    public sealed class EntitySuggestDto
    {
        // type: "patient" | "interview" | "session" | "test" | "attachment" (por ahora retornamos "patient")
        public string Type { get; set; } = "patient";
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
    }

    //// Para Fase 5 (autocomplete/sugerencias)
    //public sealed class SearchSuggestResponse
    //{
    //    public List<string> Hashtags { get; set; } = new();       // #ansiedad, #aprendizaje, …
    //    public List<LabelChipDto> Labels { get; set; } = new();   // etiqueta por code/name LIKE q
    //    public List<SearchResultItemDto> Entities { get; set; } = new(); // TOP N por nombre/código
    //    public long DurationMs { get; set; }
    //}

    public sealed class SearchSuggestResponse
    {
        public string[] Hashtags { get; set; } = Array.Empty<string>();
        public LabelSuggestDto[] Labels { get; set; } = Array.Empty<LabelSuggestDto>();
        public EntitySuggestDto[] Entities { get; set; } = Array.Empty<EntitySuggestDto>();
        public int DurationMs { get; set; }
    }
}
