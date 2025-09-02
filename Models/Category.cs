using System.ComponentModel.DataAnnotations;

namespace EPApi.Models
{
    public sealed class Category
    {
        public int Id { get; set; }

        // FK → disciplines.id
        public int DisciplineId { get; set; }

        [Required, StringLength(32, MinimumLength = 2)]
        public string Code { get; set; } = default!;

        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = default!;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // DTOs para requests
    public sealed class CategoryCreateDto
    {
        [Required]
        public int DisciplineId { get; set; }

        [Required, StringLength(32, MinimumLength = 2)]
        public string Code { get; set; } = default!;

        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = default!;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public sealed class CategoryUpdateDto
    {
        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = default!;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
