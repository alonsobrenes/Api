// Models/ClinicianReviewDtos.cs
using System;
using System.Collections.Generic;

namespace EPApi.Models
{
    public sealed class ScaleItemDto
    {
        public Guid Id { get; set; }           // question_id
        public string Code { get; set; } = ""; // question_code
        public string Text { get; set; } = "";
        public int OrderNo { get; set; }
    }

    public sealed class ScaleWithItemsDto
    {
        public Guid Id { get; set; }           // scale_id
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public Guid? ParentScaleId { get; set; }
        public List<ScaleItemDto> Items { get; set; } = new();
    }

    // ---------- Review (GET) ----------
    public sealed class AttemptReviewScaleRow
    {
        public Guid ScaleId { get; set; }
        public int? Score { get; set; }            // null si 'X'
        public bool IsUncertain { get; set; }      // true si 'X'
        public string? Notes { get; set; }
    }

    public sealed class AttemptReviewDto
    {
        public Guid ReviewId { get; set; }
        public Guid AttemptId { get; set; }
        public bool IsFinal { get; set; }
        public string? ReviewerUserId { get; set; }  // dejamos string por si el tipo no coincide
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public List<AttemptReviewScaleRow> Scales { get; set; } = new();

        // summary
        public string? AreasConflicto { get; set; }
        public string? Interrelacion { get; set; }
        public string? Estructura { get; set; }
        public string? EstructuraImpulsos { get; set; }
        public string? EstructuraAjuste { get; set; }
        public string? EstructuraMadurez { get; set; }
        public string? EstructuraRealidad { get; set; }
        public string? EstructuraExpresion { get; set; }
    }

    // ---------- Review (POST) ----------
    public sealed class ReviewScaleInputDto
    {
        public Guid ScaleId { get; set; }
        /// <summary>0,1,2 o "X"</summary>
        public string Value { get; set; } = ""; // validaremos en controller
        public string? Notes { get; set; }
    }

    public sealed class ReviewSummaryInputDto
    {
        public string? AreasConflicto { get; set; }
        public string? Interrelacion { get; set; }
        public string? Estructura { get; set; }
        public string? EstructuraImpulsos { get; set; }
        public string? EstructuraAjuste { get; set; }
        public string? EstructuraMadurez { get; set; }
        public string? EstructuraRealidad { get; set; }
        public string? EstructuraExpresion { get; set; }
    }

    public sealed class ReviewUpsertInputDto
    {
        /// <summary>true = reviewed (final); false = review_pending (borrador)</summary>
        public bool IsFinal { get; set; }
        public List<ReviewScaleInputDto> Scales { get; set; } = new();
        public ReviewSummaryInputDto? Summary { get; set; }
        public string? ReviewerUserId { get; set; } // opcional; si tus users son int, déjalo null
    }

    public sealed class CreateAttemptInputDto
    {
        public Guid TestId { get; set; }
        public Guid? PatientId { get; set; }
    }

    public sealed class CreateAttemptResultDto
    {
        public Guid AttemptId { get; set; }
        public string Status { get; set; } = "in_progress";
        public DateTime CreatedAt { get; set; }
    }

    public sealed class AttemptMetaDto
    {
        public Guid AttemptId { get; set; }
        public Guid TestId { get; set; }
        public Guid? PatientId { get; set; }
        public string Status { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

}
