-- Grants an Azure managed identity access to the database.
--
-- Uses the SID form rather than FROM EXTERNAL PROVIDER, which would require the
-- SQL Server to hold the Directory Readers role in Entra ID — a tenant-admin grant
-- that cannot be made from an application pipeline.
--
-- The SID is the identity's CLIENT ID (application ID), not its object ID, converted
-- to a little-endian byte array. See docs/decisions.md.
--
-- Run by infra/scripts/grant-db-access.ps1, which supplies $(AppName) and $(AppSid) as
-- sqlcmd variables. They are textual substitutions, which is why they can appear in
-- CREATE USER — that statement takes no runtime variables.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @name       sysname       = N'$(AppName)';
DECLARE @expectedSid varbinary(85) = $(AppSid);
DECLARE @actualSid  varbinary(85);

SELECT @actualSid = sid
FROM   sys.database_principals
WHERE  name = @name;

-- The case this block exists for.
--
-- A user-assigned identity is durable, but it is not immortal: recreate it and the
-- client ID changes while the name does not. A plain "create it if it isn't there"
-- script would then find a user with the right name, do nothing, and leave the App
-- Service authenticating against a SID that no longer belongs to it.
--
-- That failure is invisible here and surfaces much later as a login failure from the
-- application, which reads like a firewall or a connection string problem rather than a
-- stale grant. Comparing the SID is what makes re-running this script genuinely
-- idempotent instead of merely non-erroring.
IF @actualSid IS NOT NULL AND @actualSid <> @expectedSid
BEGIN
    PRINT 'Existing user [$(AppName)] carries a stale SID. Dropping and recreating.';
    DROP USER [$(AppName)];
    SET @actualSid = NULL;
END

IF @actualSid IS NULL
BEGIN
    PRINT 'Creating contained user [$(AppName)].';
    CREATE USER [$(AppName)] WITH SID = $(AppSid), TYPE = E;

    ALTER ROLE db_datareader ADD MEMBER [$(AppName)];
    ALTER ROLE db_datawriter ADD MEMBER [$(AppName)];
    -- db_ddladmin is needed because EF Core migrations run at application startup
    -- (approach.md §7), so the application itself creates and alters the schema.
    ALTER ROLE db_ddladmin  ADD MEMBER [$(AppName)];
END
ELSE
BEGIN
    PRINT 'Contained user [$(AppName)] already present with the expected SID.';
END

-- Verification, printed into the pipeline log.
--
-- USER_NAME(), not SUSER_SNAME(). Because this user was created from a SID rather than
-- FROM EXTERNAL PROVIDER, SQL never asked Entra for a display name, so the login
-- surfaces as <client-id>@<tenant-id> and a healthy connection reads as though the wrong
-- principal connected. docs/decisions.md records this; it is the first thing anyone
-- diagnosing this path will trip over.
--
-- The role memberships are included for the same reason: they prove the grants landed,
-- not merely that the user exists.
SELECT
    p.name                                             AS [user],
    p.type_desc                                        AS [type],
    CONVERT(varchar(100), p.sid, 1)                    AS [sid],
    IS_ROLEMEMBER('db_datareader', p.name)             AS [is_datareader],
    IS_ROLEMEMBER('db_datawriter', p.name)             AS [is_datawriter],
    IS_ROLEMEMBER('db_ddladmin',   p.name)             AS [is_ddladmin]
FROM sys.database_principals p
WHERE p.name = @name;
