// Controllers/TestRunsController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EPApi.DataAccess;
using EPApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/test-runs")]
    [Authorize]
    public sealed class TestRunsController : ControllerBase
    {
        private readonly ITestRepository _repo;
        public TestRunsController(ITestRepository repo) => _repo = repo;

        [HttpPost("submit")]
        public async Task<ActionResult<TestRunSubmitResultDto>> Submit([FromBody] TestRunSubmitDto dto, CancellationToken ct)
        {
            if (dto == null) return BadRequest("Body requerido.");
            if (dto.TestId == Guid.Empty) return BadRequest("TestId requerido.");
            if (dto.FinishedAtUtc < dto.StartedAtUtc) return BadRequest("Rango de fechas inválido.");

            // 1) Metadatos del test
            var questions = (await _repo.GetQuestionsAsync(dto.TestId, ct)).ToArray();
            if (questions.Length == 0) return BadRequest("El test no tiene preguntas.");
            var options = (await _repo.GetQuestionOptionsByTestAsync(dto.TestId, ct)).ToArray();
            var scales = (await _repo.GetScalesAsync(dto.TestId, ct)).ToArray();
            var map = (await _repo.GetScaleQuestionMapAsync(dto.TestId, ct)).ToArray();

            // 2) Índices útiles
            var qById = questions.ToDictionary(q => q.Id);
            var optsByQ = options
                .GroupBy(o => o.QuestionId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.OrderNo).ToArray()); // OrderNo es int (no null)

            // 3) Respuestas indexadas
            var ansByQ = (dto.Answers ?? new List<TestRunAnswerDto>())
                .GroupBy(a => a.QuestionId)
                .ToDictionary(g => g.Key, g => g.Last()); // si vino duplicada, me quedo con la última

            // 4) Helpers de puntuación
            double ScoreOf(Guid qid, TestRunAnswerDto? a)
            {
                if (!optsByQ.TryGetValue(qid, out var qopts) || qopts.Length == 0)
                {
                    // open_text: no puntúa
                    return 0.0;
                }

                // SINGLE
                if (!string.IsNullOrWhiteSpace(a?.Value))
                {
                    var v = a!.Value!.Trim();

                    // Intenta parsear como número (en el FE solemos mandar el value numérico como string)
                    if (double.TryParse(v, out var num)) return num;

                    // Si no fue número, intenta contra el label
                    var found = qopts.FirstOrDefault(o => string.Equals(o.Label, v, StringComparison.OrdinalIgnoreCase));
                    return found?.Value ?? 0.0;
                }

                // MULTI
                if (a?.Values is { Count: > 0 })
                {
                    double s = 0.0;
                    foreach (var vv in a.Values!)
                    {
                        if (double.TryParse(vv, out var n)) s += n;
                        else
                        {
                            var found = qopts.FirstOrDefault(o => string.Equals(o.Label, vv, StringComparison.OrdinalIgnoreCase));
                            s += found?.Value ?? 0.0;
                        }
                    }
                    return s;
                }

                // No contestada / omitida
                return 0.0;
            }

            (double min, double max) RangeOf(Guid qid)
            {
                if (!optsByQ.TryGetValue(qid, out var qopts) || qopts.Length == 0)
                    return (0.0, 0.0);

                var vals = qopts.Select(o => (double)o.Value).ToArray();
                return (vals.Min(), vals.Max());
            }

            // 5) Puntuar por escala
            var scalesOut = new List<TestRunScaleScore>();
            foreach (var s in scales)
            {
                var links = map.Where(m => m.ScaleId == s.Id).ToArray();
                if (links.Length == 0) continue;

                double raw = 0.0;
                double minTot = 0.0;
                double maxTot = 0.0;

                foreach (var l in links)
                {
                    var qid = l.QuestionId;
                    ansByQ.TryGetValue(qid, out var a);

                    var (min, max) = RangeOf(qid);
                    var baseScore = ScoreOf(qid, a);                    
                    var weight = l.Weight <= 0 ? 1.0 : (double)l.Weight;


                    // invertir si corresponde: min + (max - base)
                    var eff = l.Reverse ? (min + (max - baseScore)) : baseScore;

                    raw += weight * eff;
                    minTot += weight * min;
                    maxTot += weight * max;
                }

                double? percent = (maxTot > minTot) ? (100.0 * (raw - minTot) / (maxTot - minTot)) : (double?)null;

                scalesOut.Add(new TestRunScaleScore
                {
                    ScaleId = s.Id,
                    ScaleCode = s.Code,
                    ScaleName = s.Name,
                    Raw = raw,
                    Min = minTot,
                    Max = maxTot,
                    Percent = percent ?? 0.0 // tu modelo lo define como no-nullable double
                });
            }

            // 6) Total (si no hay escala TOTAL, sumar todas)
            TestRunScaleScore? total = scalesOut
                .FirstOrDefault(x => string.Equals(x.ScaleCode, "TOTAL", StringComparison.OrdinalIgnoreCase));

            if (total == null && scalesOut.Count > 0)
            {
                var raw = scalesOut.Sum(x => x.Raw);
                var min = scalesOut.Sum(x => x.Min);
                var max = scalesOut.Sum(x => x.Max);
                var pct = (max > min) ? (100.0 * (raw - min) / (max - min)) : 0.0;

                total = new TestRunScaleScore
                {
                    ScaleId = Guid.Empty,
                    ScaleCode = "TOTAL",
                    ScaleName = "Total",
                    Raw = raw,
                    Min = min,
                    Max = max,
                    Percent = pct
                };
            }

            // 7) Persistir ejecución del test
            var runId = Guid.NewGuid();
            var answersJson = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = false });

            //var save = new TestRunSave
            //{
            //    RunId = runId,
            //    TestId = dto.TestId,
            //    PatientId = dto.PatientId,
            //    AssignmentId = dto.AssignmentId,
            //    StartedAtUtc = dto.StartedAtUtc,
            //    FinishedAtUtc = dto.FinishedAtUtc,
            //    AnswersJson = answersJson,
            //    TotalRaw = total?.Raw,
            //    TotalMin = total?.Min,
            //    TotalMax = total?.Max,
            //    TotalPercent = total?.Percent,
            //    Scales = scalesOut
            //};

            //await _repo.SaveRunAsync(save, ct);

            var save = new TestRunSave
            {
                RunId = runId,
                TestId = dto.TestId,
                PatientId = dto.PatientId,
                AssignmentId = dto.AssignmentId,
                StartedAtUtc = dto.StartedAtUtc,
                FinishedAtUtc = dto.FinishedAtUtc,
                AnswersJson = answersJson,
                TotalRaw = total?.Raw,
                TotalMin = total?.Min,
                TotalMax = total?.Max,
                TotalPercent = total?.Percent,
                Scales = scalesOut.Select(s => new TestRunScaleScore
                {
                    ScaleId = s.ScaleId,
                    ScaleCode = s.ScaleCode,   // <- nombres de columnas en BD
                    ScaleName = s.ScaleName,
                    Raw = s.Raw,
                    Min = s.Min,
                    Max = s.Max,
                    Percent = s.Percent
                }).ToList()
            };

            // 8) Respuesta
            return Ok(new TestRunSubmitResultDto
            {
                RunId = runId,
                TestId = dto.TestId,
                PatientId = dto.PatientId,
                FinishedAtUtc = dto.FinishedAtUtc,
                Scales = scalesOut.Select(s => new ScaleScoreDto
                {
                    ScaleId = s.ScaleId,
                    Code = s.ScaleCode ?? "",
                    Name = s.ScaleName ?? "",
                    Raw = s.Raw,
                    Min = s.Min,
                    Max = s.Max,
                    Percent = s.Percent
                }).ToList(),
                TotalRaw = total?.Raw,
                TotalMax = total?.Max,
                TotalMin = total?.Min,
                TotalPercent = total?.Percent
            });
        }
    }
}
