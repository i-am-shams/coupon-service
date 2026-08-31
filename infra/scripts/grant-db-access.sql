-- Grants an Azure managed identity access to the database.
-- Uses the SID form rather than FROM EXTERNAL PROVIDER, which would require the
-- SQL Server to hold the Directory Readers role in Entra ID — a tenant-admin grant
-- that cannot be made from an application pipeline.
--
-- The SID is the identity's CLIENT ID (application ID), not its object ID, converted
-- to a little-endian byte array. See docs/decisions.md.

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$(AppName)')
BEGIN
    CREATE USER [$(AppName)] WITH SID = $(AppSid), TYPE = E;
    ALTER ROLE db_datareader ADD MEMBER [$(AppName)];
    ALTER ROLE db_datawriter ADD MEMBER [$(AppName)];
    ALTER ROLE db_ddladmin  ADD MEMBER [$(AppName)];
END