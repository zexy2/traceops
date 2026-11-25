-- Creates the dedicated database, its ownership marker and a least-privilege
-- runtime login. Run as sa with: -v AppPassword="..."
SET NOCOUNT ON;
IF DB_ID(N'TraceOpsLabDB') IS NULL
    CREATE DATABASE [TraceOpsLabDB];
GO
USE [TraceOpsLabDB];
GO
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE class = 0 AND name = N'TraceOps.M4.Owner')
    EXEC sys.sp_addextendedproperty
        @name = N'TraceOps.M4.Owner',
        @value = N'TraceOpsLabDB:M4-SQL-INTEGRATION:v1';
GO
IF SUSER_ID(N'traceops_app') IS NULL
    CREATE LOGIN [traceops_app] WITH PASSWORD = N'$(AppPassword)',
        DEFAULT_DATABASE = [TraceOpsLabDB], CHECK_POLICY = ON;
GO
-- The runtime user only gets CONNECT here; EXECUTE is granted per schema.
IF USER_ID(N'traceops_app') IS NULL
    CREATE USER [traceops_app] FOR LOGIN [traceops_app];
GO
