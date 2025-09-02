// EPApi/Models/TestRunDtos.cs
using System;
using System.Collections.Generic;

namespace EPApi.Models
{
    /// <summary>
    /// Mapa (escala ←→ pregunta) para cálculo de puntajes.
    /// </summary>
    //public sealed class TestScaleQuestionRow
    //{
    //    public Guid ScaleId { get; set; }
    //    public Guid QuestionId { get; set; }
    //    public double Weight { get; set; }
    //    public bool Reverse { get; set; }
    //}

    /// <summary>
    /// Respuesta de un ítem en un "run".
    /// Value se guarda como string (p.ej. "2", "Sí", "Siempre", texto libre).
    /// </summary>
    public sealed class TestRunSaveAnswer
    {
        public Guid QuestionId { get; set; }
        public string? Value { get; set; }
    }

    /// <summary>
    /// Payload de guardado de una ejecución de test (run).
    /// </summary>
    public sealed class TestRunSave
    {
        public Guid RunId { get; set; }
        public Guid TestId { get; set; }
        public Guid? PatientId { get; set; }
        public Guid? AssignmentId { get; set; }
        public int? ClinicianUserId { get; set; } // opcional: puedes setearlo a partir del UserId del token
        public DateTime StartedAtUtc { get; set; }
        public DateTime FinishedAtUtc { get; set; }
        public string AnswersJson { get; set; } = "";

        public double? TotalRaw { get; set; }
        public double? TotalMin { get; set; }
        public double? TotalMax { get; set; }
        public double? TotalPercent { get; set; }

        public List<TestRunScaleScore> Scales { get; set; } = new();
    }

    public sealed class TestRunScaleScoreSave
    {
        public Guid ScaleId { get; set; }
        public string ScaleCode { get; set; } = ""; // coincide con columna scale_code
        public string ScaleName { get; set; } = ""; // coincide con columna scale_name
        public double Raw { get; set; }             // raw_score
        public double Min { get; set; }             // min_score
        public double Max { get; set; }             // max_score
        public double? Percent { get; set; }        // percent (NULLable)
    }


    public sealed class TestRunAnswerSave
    {
        public Guid QuestionId { get; set; }

        // Si marcó opción, guarda el valor de esa opción (e.g. 0/1/2, 1..4, etc.)
        public int? OptionValue { get; set; }

        // Si la respuesta es abierta (texto)
        public string? Raw { get; set; }
    }

    public sealed class TestRunScaleScore
    {
        public Guid ScaleId { get; set; }
        public string? ScaleCode { get; set; }
        public string? ScaleName { get; set; }

        public double Raw { get; set; }     // suma ponderada obtenida
        public double Min { get; set; }     // mínimo teórico
        public double Max { get; set; }     // máximo teórico
        public double Percent { get; set; } // Raw normalizado [0–100]
    }

    // Si aún no existe en tu proyecto, añade este row simple:
    public sealed class TestScaleQuestionRow
    {
        public Guid ScaleId { get; set; }
        public Guid QuestionId { get; set; }
        public int? OrderNo { get; set; }
        public double Weight { get; set; } = 1.0;
        public bool Reverse { get; set; } = false;
    }
}
