DECLARE @OrgId UNIQUEIDENTIFIER = '7A1A5713-118D-4362-A2CB-F3CE164E3CCA';

-- Límite en GB → bytes
WITH limit_cte AS (
  SELECT TOP (1)
         (limit_value * 1024 * 1024 * 1024) AS limit_bytes
  FROM dbo.entitlements
  WHERE org_id = @OrgId
    AND feature_code = 'storage.gb'
),
used_cte AS (
  SELECT SUM(CASE WHEN deleted_at_utc IS NULL THEN byte_size ELSE 0 END) AS used_bytes
  FROM dbo.patient_files
  WHERE org_id = @OrgId
)
SELECT u.used_bytes, l.limit_bytes,
       CASE WHEN l.limit_bytes > 0 THEN (100.0 * u.used_bytes / l.limit_bytes) ELSE NULL END AS used_pct
FROM used_cte u CROSS JOIN limit_cte l;
