-- Creates the schema the web app uses in SQL mode. Run as sa against
-- TraceOpsLabDB with: -v Schema="m4_<32 hex>" RunId="<same GUID with dashes>"
-- then run src/TraceOps.SqlServer/Migrations/001_M4.sql and 03-grant.sql.
SET NOCOUNT ON;
IF SCHEMA_ID(N'$(Schema)') IS NULL
BEGIN
    EXEC (N'CREATE SCHEMA [$(Schema)] AUTHORIZATION dbo');
    EXEC sys.sp_addextendedproperty
        @name = N'TraceOps.M4.Run', @value = N'$(RunId)',
        @level0type = N'SCHEMA', @level0name = N'$(Schema)';
END
GO
