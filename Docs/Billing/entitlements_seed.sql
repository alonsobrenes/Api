
---

# `db/seeds/entitlements_seed.sql`

> Script idempotente para **aplicar/actualizar** entitlements a una organización según un `@PlanCode`.  
> Incluye dos variantes para la columna del código del entitlement en la tabla `entitlements`: `code` **o** `feature_code`. **Descomenta una u otra** según tu esquema.

```sql
/* db/seeds/entitlements_seed.sql
   Aplica entitlements por plan a una organización específica.
   - Idempotente (usa MERGE).
   - Ajusta @OrgId y @PlanCode.
   - Descomenta el MERGE que corresponda a tu esquema:
       · Variante A: la columna se llama [code]
       · Variante B: la columna se llama [feature_code]
*/

SET NOCOUNT ON;

DECLARE @OrgId     UNIQUEIDENTIFIER = '7A1A5713-118D-4362-A2CB-F3CE164E3CCA';
DECLARE @PlanCode  NVARCHAR(50)     = 'solo';   -- 'solo' | 'clinic' | 'pro'
DECLARE @UtcNow    DATETIME2(7)     = SYSUTCDATETIME();

-- Normalizamos a minúsculas para comparar
SET @PlanCode = LOWER(@PlanCode);

IF OBJECT_ID('tempdb..#limits') IS NOT NULL DROP TABLE #limits;
CREATE TABLE #limits (
  code        NVARCHAR(50) NOT NULL,
  limit_value INT          NOT NULL
);

-- Cargar mapa por plan
IF (@PlanCode = 'solo')
BEGIN
  INSERT INTO #limits(code, limit_value) VALUES
    ('ai.opinion.monthly', 50),
    ('tests.auto.monthly', 20),
    ('sacks.monthly',      5),
    ('seats',              1),
    ('storage.gb',         10);
END
ELSE IF (@PlanCode = 'clinic')
BEGIN
  INSERT INTO #limits(code, limit_value) VALUES
    ('ai.opinion.monthly', 200),
    ('tests.auto.monthly', 100),
    ('sacks.monthly',      20),
    ('seats',              5),
    ('storage.gb',         50);
END
ELSE IF (@PlanCode = 'pro')
BEGIN
  INSERT INTO #limits(code, limit_value) VALUES
    ('ai.opinion.monthly', 1000),
    ('tests.auto.monthly', 500),
    ('sacks.monthly',      100),
    ('seats',              20),
    ('storage.gb',         200);
END
ELSE
BEGIN
  RAISERROR('PlanCode desconocido. Use solo | clinic | pro', 16, 1);
  RETURN;
END

/* =======================================================================================
   VARIANTE A — usa [code] como nombre de columna del entitlement en dbo.entitlements
   ---------------------------------------------------------------------------------------
   Descomenta este bloque si tu tabla dbo.entitlements tiene columnas: 
     org_id UNIQUEIDENTIFIER, code NVARCHAR(50), limit_value INT, updated_at_utc DATETIME2(7), ...
---------------------------------------------------------------------------------------*/
/*
MERGE dbo.entitlements AS tgt
USING (
  SELECT @OrgId AS org_id, code, limit_value
  FROM #limits
) AS src
ON  tgt.org_id = src.org_id
AND tgt.code   = src.code
WHEN MATCHED THEN
  UPDATE SET 
    tgt.limit_value    = src.limit_value,
    tgt.updated_at_utc = @UtcNow
WHEN NOT MATCHED BY TARGET THEN
  INSERT (org_id, code, limit_value, updated_at_utc)
  VALUES (src.org_id, src.code, src.limit_value, @UtcNow)
WHEN NOT MATCHED BY SOURCE AND tgt.org_id = @OrgId THEN
  DELETE
;
*/

/* =======================================================================================
   VARIANTE B — usa [feature_code] como nombre de columna del entitlement en dbo.entitlements
   -------------------------------------------------------------------------------------------
   Descomenta este bloque si tu tabla dbo.entitlements tiene columnas:
     org_id UNIQUEIDENTIFIER, feature_code NVARCHAR(50), limit_value INT, updated_at_utc DATETIME2(7), ...
-------------------------------------------------------------------------------------------*/
/*
MERGE dbo.entitlements AS tgt
USING (
  SELECT @OrgId AS org_id, code AS feature_code, limit_value
  FROM #limits
) AS src
ON  tgt.org_id      = src.org_id
AND tgt.feature_code = src.feature_code
WHEN MATCHED THEN
  UPDATE SET 
    tgt.limit_value    = src.limit_value,
    tgt.updated_at_utc = @UtcNow
WHEN NOT MATCHED BY TARGET THEN
  INSERT (org_id, feature_code, limit_value, updated_at_utc)
  VALUES (src.org_id, src.feature_code, src.limit_value, @UtcNow)
WHEN NOT MATCHED BY SOURCE AND tgt.org_id = @OrgId THEN
  DELETE
;
*/

-- Verificación
SELECT *
FROM dbo.entitlements
WHERE org_id = @OrgId
ORDER BY /* usa la columna correcta */ code /* ó feature_code */;
