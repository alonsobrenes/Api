--6D318048-0D53-4695-A196-57C9CC4B94E4
--7A1A5713-118D-4362-A2CB-F3CE164E3CCA
DECLARE @OrgId UNIQUEIDENTIFIER = '7A1A5713-118D-4362-A2CB-F3CE164E3CCA';

SELECT TOP (1)
       /* usa la columna correcta */ feature_code /* ó feature_code */ AS entitlement,
       limit_value AS gb_limit,
       (limit_value * 1024.0 * 1024.0 * 1024.0) AS bytes_limit
FROM dbo.entitlements
WHERE org_id = @OrgId
  AND ( /* usa la columna correcta */ feature_code /* ó feature_code */ ) = 'storage.gb';