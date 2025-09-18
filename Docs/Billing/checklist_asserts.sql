/* ops/checklist_asserts.sql
   Checklist de consistencia para EP (ejecutar en cada deploy).
   - Usa @OrgId para filtrar por una org, NULL para revisar todas.
   - Lanza RAISERROR (16) en fallas críticas.
*/

SET NOCOUNT ON;

DECLARE @OrgId UNIQUEIDENTIFIER = '7A1A5713-118D-4362-A2CB-F3CE164E3CCA';  -- p.ej. '7A1A5713-118D-4362-A2CB-F3CE164E3CCA' o NULL para todas
DECLARE @Now  DATETIME2(7) = SYSUTCDATETIME();

/* =======================================================================================
   0) Guardas mínimas de esquema (no falla si faltan; solo informa)
======================================================================================= */
PRINT '0) CHEQUEO DE ESQUEMA (informativo)';
IF COL_LENGTH('dbo.entitlements','feature_code') IS NULL
BEGIN
  PRINT 'ADVERTENCIA: dbo.entitlements.feature_code no existe. Revisa el esquema.';
END
IF OBJECT_ID('dbo.patient_files','U') IS NULL
BEGIN
  PRINT 'ADVERTENCIA: dbo.patient_files no existe. Revisa el esquema.';
END
IF OBJECT_ID('dbo.org_storage','U') IS NULL
BEGIN
  PRINT 'ADVERTENCIA: dbo.org_storage no existe. Revisa el esquema.';
END
IF OBJECT_ID('dbo.subscriptions','U') IS NULL
BEGIN
  PRINT 'ADVERTENCIA: dbo.subscriptions no existe. Revisa el esquema.';
END
IF OBJECT_ID('dbo.usage_counters','U') IS NULL
BEGIN
  PRINT 'ADVERTENCIA: dbo.usage_counters no existe. Revisa el esquema.';
END
IF OBJECT_ID('dbo.webhook_idempotency','U') IS NULL
BEGIN
  PRINT 'ADVERTENCIA: dbo.webhook_idempotency no existe. Revisa el esquema.';
END

/* =======================================================================================
   1) ENTITLEMENTS — Unicidad por (org_id, feature_code) y set esperado
======================================================================================= */
PRINT '1) ENTITLEMENTS';

;WITH dups AS (
  SELECT org_id, feature_code, COUNT(*) AS n
  FROM dbo.entitlements
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
  GROUP BY org_id, feature_code
  HAVING COUNT(*) > 1
)
SELECT * FROM dups;

IF EXISTS (SELECT 1 FROM (
  SELECT org_id, feature_code, COUNT(*) AS n
  FROM dbo.entitlements
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
  GROUP BY org_id, feature_code
  HAVING COUNT(*) > 1
) AS dups)
BEGIN
  RAISERROR('Duplicados en entitlements (org_id, feature_code). Debe haber una fila por feature/org.', 16, 1);
END

-- (Opcional) Validar set conocido de features – avisar si hay desconocidos
;WITH known AS (
  SELECT v FROM (VALUES
    (N'ai.credits.monthly'),
    (N'tests.auto.monthly'),
    (N'sacks.monthly'),
    (N'seats'),
    (N'storage.gb')
  ) AS t(v)
),
unknown AS (
  SELECT e.org_id, e.feature_code
  FROM dbo.entitlements e
  LEFT JOIN known k ON k.v = e.feature_code
  WHERE (@OrgId IS NULL OR e.org_id = @OrgId)
    AND k.v IS NULL
)
SELECT * FROM unknown;

IF EXISTS (SELECT 1 FROM (
  SELECT e.org_id, e.feature_code
  FROM dbo.entitlements e
  LEFT JOIN (VALUES
    (N'ai.credits.monthly'),
    (N'tests.auto.monthly'),
    (N'sacks.monthly'),
    (N'seats'),
    (N'storage.gb')
  ) AS k(v) ON k.v = e.feature_code
  WHERE (@OrgId IS NULL OR e.org_id = @OrgId)
    AND k.v IS NULL
) AS unknown)
BEGIN
  PRINT 'AVISO: Hay entitlements con feature_code fuera del set conocido (esto puede ser válido si agregaste nuevos).';
END

/* =======================================================================================
   2) USAGE_COUNTERS — Unicidad lógica y período
======================================================================================= */
PRINT '2) USAGE_COUNTERS';

-- Duplicados lógicos (aunque tengas UQ, detecta si falta el índice)
;WITH dups AS (
  SELECT org_id, feature_code, period_start_utc, period_end_utc, COUNT(*) AS n
  FROM dbo.usage_counters
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
  GROUP BY org_id, feature_code, period_start_utc, period_end_utc
  HAVING COUNT(*) > 1
)
SELECT * FROM dups;

IF EXISTS (SELECT 1 FROM (
  SELECT org_id, feature_code, period_start_utc, period_end_utc, COUNT(*) AS n
  FROM dbo.usage_counters
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
  GROUP BY org_id, feature_code, period_start_utc, period_end_utc
  HAVING COUNT(*) > 1
) AS dups)
BEGIN
  RAISERROR('Duplicados en usage_counters (org_id, feature_code, period_start_utc, period_end_utc).', 16, 1);
END

-- Periodos invertidos (start >= end)
;WITH bad AS (
  SELECT *
  FROM dbo.usage_counters
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
    AND period_start_utc >= period_end_utc
)
SELECT * FROM bad;

IF EXISTS (SELECT 1 FROM (
  SELECT 1 AS dummy
  FROM dbo.usage_counters
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
    AND period_start_utc >= period_end_utc
) AS z)
BEGIN
  RAISERROR('usage_counters con periodos inválidos (start >= end).', 16, 1);
END

/* =======================================================================================
   3) STORAGE — Reconciliación org_storage.used_bytes vs patient_files activos
======================================================================================= */
PRINT '3) STORAGE RECONCILIATION';

;WITH pf AS (
  SELECT org_id, SUM(CASE WHEN deleted_at_utc IS NULL THEN byte_size ELSE 0 END) AS used_pf
  FROM dbo.patient_files
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
  GROUP BY org_id
),
os AS (
  SELECT org_id, used_bytes
  FROM dbo.org_storage
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
),
cmp AS (
  SELECT COALESCE(pf.org_id, os.org_id) AS org_id,
         COALESCE(pf.used_pf, 0) AS used_pf,
         COALESCE(os.used_bytes, 0) AS used_bytes,
         (COALESCE(os.used_bytes, 0) - COALESCE(pf.used_pf, 0)) AS diff
  FROM pf
  FULL OUTER JOIN os ON os.org_id = pf.org_id
)
SELECT * FROM cmp WHERE diff <> 0;

IF EXISTS (SELECT 1 FROM (
  SELECT (COALESCE(os.used_bytes, 0) - COALESCE(pf.used_pf, 0)) AS diff
  FROM dbo.org_storage os
  FULL OUTER JOIN (
    SELECT org_id, SUM(CASE WHEN deleted_at_utc IS NULL THEN byte_size ELSE 0 END) AS used_pf
    FROM dbo.patient_files
    WHERE (@OrgId IS NULL OR org_id = @OrgId)
    GROUP BY org_id
  ) pf ON pf.org_id = os.org_id
  WHERE (@OrgId IS NULL OR COALESCE(os.org_id, pf.org_id) = @OrgId)
) AS q WHERE diff <> 0)
BEGIN
  RAISERROR('org_storage.used_bytes no coincide con SUM(patient_files.byte_size WHERE deleted_at_utc IS NULL).', 16, 1);
END

/* =======================================================================================
   4) SUBSCRIPTIONS — Ventanas activas y TRIAL razonable
======================================================================================= */
PRINT '4) SUBSCRIPTIONS';

-- start/end nulos o invertidos
;WITH bad AS (
  SELECT *
  FROM dbo.subscriptions
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
    AND (
      current_period_start_utc IS NULL
      OR current_period_end_utc   IS NULL
      OR current_period_start_utc >= current_period_end_utc
    )
)
SELECT * FROM bad;

IF EXISTS (SELECT 1 FROM (
  SELECT 1 AS dummy
  FROM dbo.subscriptions
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
    AND (
      current_period_start_utc IS NULL
      OR current_period_end_utc   IS NULL
      OR current_period_start_utc >= current_period_end_utc
    )
) AS z)
BEGIN
  RAISERROR('Suscripciones con ventana inválida (fechas nulas o start >= end).', 16, 1);
END

-- TRIAL de ~7 días (tolerancia 2 horas)
;WITH tr AS (
  SELECT org_id, DATEDIFF(MINUTE, current_period_start_utc, current_period_end_utc) AS mins
  FROM dbo.subscriptions
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
    AND LOWER(plan_code) = 'trial'
)
SELECT * FROM tr WHERE mins NOT BETWEEN (7*24*60 - 120) AND (7*24*60 + 120);

IF EXISTS (SELECT 1 FROM (
  SELECT 1 AS dummy
  FROM dbo.subscriptions
  WHERE (@OrgId IS NULL OR org_id = @OrgId)
    AND LOWER(plan_code) = 'trial'
    AND DATEDIFF(MINUTE, current_period_start_utc, current_period_end_utc) NOT BETWEEN (7*24*60 - 120) AND (7*24*60 + 120)
) AS z)
BEGIN
  RAISERROR('TRIAL no tiene una duración cercana a 7 días (±2h).', 16, 1);
END

/* =======================================================================================
   5) WEBHOOK IDEMPOTENCY — Chequeo básico (informativo)
======================================================================================= */
PRINT '5) WEBHOOK IDEMPOTENCY (info)';

-- Lista los últimos 20 eventos para inspección manual
SELECT TOP (20) *
FROM dbo.webhook_idempotency
WHERE (@OrgId IS NULL OR org_id = @OrgId)
ORDER BY received_at_utc DESC;

PRINT 'Checklist COMPLETADO.';