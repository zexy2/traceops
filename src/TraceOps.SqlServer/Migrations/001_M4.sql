-- Substitute only a validated m4_<32 lowercase hex digits> identifier.
-- Execute the GO batches in order on the fixture connection. Stop at the first
-- error. The ledger is stamped only after all four procedures have been created.
SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;

DECLARE @Schema sysname = N'$(Schema)';
DECLARE @DatabaseName sysname = N'TraceOpsLabDB';
DECLARE @DatabaseMarker nvarchar(64) = N'TraceOpsLabDB:M4-SQL-INTEGRATION:v1';

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> @DatabaseName
   OR DATALENGTH(DB_NAME()) <> DATALENGTH(@DatabaseName)
    THROW 51090, 'M4 requires its dedicated database.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.extended_properties
    WHERE class = 0 AND major_id = 0 AND minor_id = 0
      AND name COLLATE Latin1_General_100_BIN2 = N'TraceOps.M4.Owner'
      AND DATALENGTH(name) = DATALENGTH(N'TraceOps.M4.Owner')
      AND CONVERT(nvarchar(4000), value) COLLATE Latin1_General_100_BIN2 = @DatabaseMarker
      AND DATALENGTH(CONVERT(nvarchar(4000), value)) = DATALENGTH(@DatabaseMarker)
)
    THROW 51090, 'M4 database ownership marker mismatch.', 1;

IF DATALENGTH(@Schema) <> 70
   OR LEFT(@Schema, 3) COLLATE Latin1_General_100_BIN2 <> N'm4_'
   OR SUBSTRING(@Schema, 4, 32) COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9a-f]%'
    THROW 51090, 'M4 requires a validated GUID schema.', 1;

DECLARE @SchemaId int = SCHEMA_ID(@Schema);
DECLARE @RunMarker nvarchar(36) = CONCAT(
    SUBSTRING(@Schema, 4, 8), N'-', SUBSTRING(@Schema, 12, 4), N'-',
    SUBSTRING(@Schema, 16, 4), N'-', SUBSTRING(@Schema, 20, 4), N'-', SUBSTRING(@Schema, 24, 12));

IF NOT EXISTS
(
    SELECT 1
    FROM sys.schemas AS s
    JOIN sys.extended_properties AS p ON p.class = 3 AND p.major_id = s.schema_id AND p.minor_id = 0
    WHERE s.schema_id = @SchemaId
      AND s.name COLLATE Latin1_General_100_BIN2 = @Schema AND DATALENGTH(s.name) = DATALENGTH(@Schema)
      AND p.name COLLATE Latin1_General_100_BIN2 = N'TraceOps.M4.Run'
      AND DATALENGTH(p.name) = DATALENGTH(N'TraceOps.M4.Run')
      AND CONVERT(nvarchar(4000), p.value) COLLATE Latin1_General_100_BIN2 = @RunMarker
      AND DATALENGTH(CONVERT(nvarchar(4000), p.value)) = DATALENGTH(@RunMarker)
)
    THROW 51090, 'M4 schema ownership marker mismatch.', 1;

IF EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = @SchemaId)
   OR EXISTS (SELECT 1 FROM sys.types WHERE schema_id = @SchemaId AND is_user_defined = 1)
   OR EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE schema_id = @SchemaId)
    THROW 51090, 'M4 requires an empty owned schema.', 1;

BEGIN TRANSACTION;

CREATE TABLE [$(Schema)].[SchemaVersion]
(
    [Version] int NOT NULL PRIMARY KEY,
    [MigrationId] varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL
);

-- Map each permitted byte to 'A' under BIN2, then compare the actual bytes.
-- The expected string has the original byte length, including any trailing blanks.
CREATE TABLE [$(Schema)].[Shipments]
(
    [TrackingNumber] varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    [RegisteredAt] datetimeoffset(7) NOT NULL,
    [Status] tinyint NOT NULL CHECK ([Status] BETWEEN 0 AND 6),
    [LastOccurredAt] datetimeoffset(7) NOT NULL,
    [Version] bigint NOT NULL CHECK ([Version] >= 0),
    CHECK (DATALENGTH([TrackingNumber]) BETWEEN 1 AND 32 AND
        CONVERT(varbinary(32), TRANSLATE([TrackingNumber] COLLATE Latin1_General_100_BIN2,
            'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-', REPLICATE('A', 37)))
        = CONVERT(varbinary(32), REPLICATE('A', DATALENGTH([TrackingNumber])))),
    CHECK (DATEPART(TZOFFSET, [RegisteredAt]) = 0 AND DATEPART(TZOFFSET, [LastOccurredAt]) = 0),
    CHECK ([Version] <> 0 OR ([Status] = 0 AND [LastOccurredAt] = [RegisteredAt]))
);

CREATE TABLE [$(Schema)].[ShipmentEvents]
(
    [TrackingNumber] varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [EventSequence] bigint NOT NULL CHECK ([EventSequence] > 0),
    [EventId] uniqueidentifier NOT NULL CHECK ([EventId] <> '00000000-0000-0000-0000-000000000000'),
    [Status] tinyint NOT NULL CHECK ([Status] BETWEEN 0 AND 6),
    [OccurredAt] datetimeoffset(7) NOT NULL CHECK (DATEPART(TZOFFSET, [OccurredAt]) = 0),
    [SourceSystem] varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PRIMARY KEY ([TrackingNumber], [EventSequence]),
    UNIQUE ([TrackingNumber], [EventId]),
    FOREIGN KEY ([TrackingNumber]) REFERENCES [$(Schema)].[Shipments] ([TrackingNumber]),
    CHECK (DATALENGTH([TrackingNumber]) BETWEEN 1 AND 32 AND
        CONVERT(varbinary(32), TRANSLATE([TrackingNumber] COLLATE Latin1_General_100_BIN2,
            'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-', REPLICATE('A', 37)))
        = CONVERT(varbinary(32), REPLICATE('A', DATALENGTH([TrackingNumber])))),
    CHECK (DATALENGTH([SourceSystem]) BETWEEN 1 AND 64 AND
        CONVERT(varbinary(64), TRANSLATE([SourceSystem] COLLATE Latin1_General_100_BIN2,
            'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-', REPLICATE('A', 65)))
        = CONVERT(varbinary(64), REPLICATE('A', DATALENGTH([SourceSystem]))))
);

COMMIT TRANSACTION;
GO

CREATE PROCEDURE [$(Schema)].[Shipment_List]
AS
BEGIN
    SET NOCOUNT ON;
    SET IMPLICIT_TRANSACTIONS OFF;
    IF @@LOCK_TIMEOUT < 0 OR @@LOCK_TIMEOUT > 8000 SET LOCK_TIMEOUT 8000;
    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;

    BEGIN TRY
        IF @OwnTransaction = 1
        BEGIN
            SET XACT_ABORT ON;
            SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
            BEGIN TRANSACTION;
        END
        ELSE
            SET XACT_ABORT OFF;

        IF NOT EXISTS
        (
            SELECT 1 FROM [$(Schema)].[SchemaVersion] WITH (HOLDLOCK)
            HAVING COUNT_BIG(*) = 1 AND COUNT_BIG(CASE WHEN [Version] = 1
                AND [MigrationId] = 'M4.1' AND DATALENGTH([MigrationId]) = 4 THEN 1 END) = 1
        )
            THROW 51013, 'Shipment schema version mismatch.', 1;

        SELECT [TrackingNumber], [RegisteredAt], [Status], [LastOccurredAt], [Version]
        FROM [$(Schema)].[Shipments] WITH (UPDLOCK, HOLDLOCK)
        ORDER BY [TrackingNumber];

        SELECT [TrackingNumber], [EventSequence], [EventId], [Status], [OccurredAt], [SourceSystem]
        FROM [$(Schema)].[ShipmentEvents] WITH (HOLDLOCK)
        ORDER BY [TrackingNumber], [EventSequence];

        IF @OwnTransaction = 1 COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE [$(Schema)].[Shipment_Find]
    @TrackingNumber nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET IMPLICIT_TRANSACTIONS OFF;
    IF @@LOCK_TIMEOUT < 0 OR @@LOCK_TIMEOUT > 8000 SET LOCK_TIMEOUT 8000;
    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;

    BEGIN TRY
        IF @OwnTransaction = 1
        BEGIN
            SET XACT_ABORT ON;
            SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
            BEGIN TRANSACTION;
        END
        ELSE
            SET XACT_ABORT OFF;

        IF NOT EXISTS
        (
            SELECT 1 FROM [$(Schema)].[SchemaVersion] WITH (HOLDLOCK)
            HAVING COUNT_BIG(*) = 1 AND COUNT_BIG(CASE WHEN [Version] = 1
                AND [MigrationId] = 'M4.1' AND DATALENGTH([MigrationId]) = 4 THEN 1 END) = 1
        )
            THROW 51013, 'Shipment schema version mismatch.', 1;

        -- An invalid key stays NULL. Both SELECTs keep their normal row metadata.
        DECLARE @Key varchar(32), @Bytes bigint = DATALENGTH(@TrackingNumber);
        IF @Bytes BETWEEN 2 AND 64
        BEGIN
            DECLARE @Position int = 1, @Unit int;
            WHILE @Position <= @Bytes / 2
            BEGIN
                SET @Unit = UNICODE(SUBSTRING(@TrackingNumber COLLATE Latin1_General_100_BIN2, @Position, 1));
                IF @Unit IS NULL OR NOT (@Unit = 45 OR @Unit BETWEEN 48 AND 57 OR @Unit BETWEEN 65 AND 90)
                    BREAK;
                SET @Position += 1;
            END;
            IF @Position > @Bytes / 2 SET @Key = CONVERT(varchar(32), @TrackingNumber);
        END;

        SELECT [TrackingNumber], [RegisteredAt], [Status], [LastOccurredAt], [Version]
        FROM [$(Schema)].[Shipments] WITH (UPDLOCK, HOLDLOCK)
        WHERE [TrackingNumber] = @Key COLLATE Latin1_General_100_BIN2
          AND DATALENGTH([TrackingNumber]) = DATALENGTH(@Key)
        ORDER BY [TrackingNumber];

        SELECT [TrackingNumber], [EventSequence], [EventId], [Status], [OccurredAt], [SourceSystem]
        FROM [$(Schema)].[ShipmentEvents] WITH (HOLDLOCK)
        WHERE [TrackingNumber] = @Key COLLATE Latin1_General_100_BIN2
          AND DATALENGTH([TrackingNumber]) = DATALENGTH(@Key)
        ORDER BY [EventSequence];

        IF @OwnTransaction = 1 COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE [$(Schema)].[Shipment_TryRegister]
    @TrackingNumber nvarchar(max),
    @RegisteredAt datetimeoffset(7),
    @ResultCode varchar(32) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET IMPLICIT_TRANSACTIONS OFF;
    IF @@LOCK_TIMEOUT < 0 OR @@LOCK_TIMEOUT > 8000 SET LOCK_TIMEOUT 8000;

    DECLARE @Bytes bigint = DATALENGTH(@TrackingNumber), @Position int = 1, @Unit int;
    SET @ResultCode = 'INVALID_TRACKING_NUMBER';
    IF @Bytes IS NULL OR @Bytes NOT BETWEEN 2 AND 64 RETURN;
    WHILE @Position <= @Bytes / 2
    BEGIN
        SET @Unit = UNICODE(SUBSTRING(@TrackingNumber COLLATE Latin1_General_100_BIN2, @Position, 1));
        IF @Unit IS NULL OR NOT (@Unit = 45 OR @Unit BETWEEN 48 AND 57 OR @Unit BETWEEN 65 AND 90)
            RETURN;
        SET @Position += 1;
    END;
    IF @RegisteredAt IS NULL OR DATEPART(TZOFFSET, @RegisteredAt) <> 0
    BEGIN
        SET @ResultCode = 'TIMESTAMP_MUST_BE_UTC';
        RETURN;
    END;

    DECLARE @Key varchar(32) = CONVERT(varchar(32), @TrackingNumber);
    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
    SET @ResultCode = NULL;
    BEGIN TRY
        IF @OwnTransaction = 1
        BEGIN
            SET XACT_ABORT ON;
            SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
            BEGIN TRANSACTION;
        END;

        -- The PK lookup locks the missing range as well as an existing key.
        IF EXISTS
        (
            SELECT 1 FROM [$(Schema)].[Shipments] WITH (UPDLOCK, HOLDLOCK)
            WHERE [TrackingNumber] = @Key COLLATE Latin1_General_100_BIN2
              AND DATALENGTH([TrackingNumber]) = DATALENGTH(@Key)
        )
            SET @ResultCode = 'TRACKING_NUMBER_CONFLICT';
        ELSE
        BEGIN
            INSERT INTO [$(Schema)].[Shipments] ([TrackingNumber], [RegisteredAt], [Status], [LastOccurredAt], [Version])
            VALUES (@Key, @RegisteredAt, 0, @RegisteredAt, 0);
            IF @@ROWCOUNT <> 1 THROW 51011, 'Shipment registration did not insert one row.', 1;
            SET @ResultCode = 'REGISTERED';
        END;

        IF @OwnTransaction = 1 COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE [$(Schema)].[Shipment_AppendEvent]
    @TrackingNumber nvarchar(max),
    @ExpectedVersion bigint,
    @EventId uniqueidentifier,
    @Status int,
    @OccurredAt datetimeoffset(7),
    @SourceSystem nvarchar(max),
    @ResultCode varchar(24) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @@LOCK_TIMEOUT < 0 OR @@LOCK_TIMEOUT > 8000 SET LOCK_TIMEOUT 8000;
    SET @ResultCode = NULL;

    IF @@TRANCOUNT = 0 OR XACT_STATE() <> 1
        THROW 51012, 'Append requires an active serializable caller transaction.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id = @@SPID AND transaction_isolation_level = 4)
        THROW 51012, 'Append requires an active serializable caller transaction.', 1;

    DECLARE @KeyBytes bigint = DATALENGTH(@TrackingNumber), @SourceBytes bigint = DATALENGTH(@SourceSystem);
    IF @KeyBytes IS NULL OR @KeyBytes NOT BETWEEN 2 AND 64
       OR @SourceBytes IS NULL OR @SourceBytes NOT BETWEEN 2 AND 128
       OR @EventId IS NULL OR @EventId = '00000000-0000-0000-0000-000000000000'
       OR @Status IS NULL OR @Status NOT BETWEEN 0 AND 6
       OR @OccurredAt IS NULL OR DATEPART(TZOFFSET, @OccurredAt) <> 0
       OR @ExpectedVersion IS NULL OR @ExpectedVersion < 0
        THROW 51010, 'Invalid shipment append input.', 1;

    DECLARE @Position int = 1, @Unit int;
    WHILE @Position <= @KeyBytes / 2
    BEGIN
        SET @Unit = UNICODE(SUBSTRING(@TrackingNumber COLLATE Latin1_General_100_BIN2, @Position, 1));
        IF @Unit IS NULL OR NOT (@Unit = 45 OR @Unit BETWEEN 48 AND 57 OR @Unit BETWEEN 65 AND 90)
            THROW 51010, 'Invalid shipment tracking number.', 1;
        SET @Position += 1;
    END;

    SET @Position = 1;
    WHILE @Position <= @SourceBytes / 2
    BEGIN
        SET @Unit = UNICODE(SUBSTRING(@SourceSystem COLLATE Latin1_General_100_BIN2, @Position, 1));
        IF @Unit IS NULL OR NOT (@Unit IN (45, 46, 95) OR @Unit BETWEEN 48 AND 57
                                OR @Unit BETWEEN 65 AND 90 OR @Unit BETWEEN 97 AND 122)
            THROW 51010, 'Invalid shipment event source.', 1;
        SET @Position += 1;
    END;

    DECLARE @Key varchar(32) = CONVERT(varchar(32), @TrackingNumber);
    DECLARE @Source varchar(64) = CONVERT(varchar(64), @SourceSystem);
    DECLARE @Version bigint, @ParentStatus tinyint, @ParentTime datetimeoffset(7);
    SELECT @Version = [Version], @ParentStatus = [Status], @ParentTime = [LastOccurredAt]
    FROM [$(Schema)].[Shipments] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TrackingNumber] = @Key COLLATE Latin1_General_100_BIN2
      AND DATALENGTH([TrackingNumber]) = DATALENGTH(@Key);

    IF @Version IS NULL OR @Version <> @ExpectedVersion
        THROW 51011, 'Shipment missing or version mismatch.', 1;

    -- Sequences are positive and unique. Count = max = version proves there
    -- are no gaps. The last persisted row must describe the locked projection.
    IF NOT EXISTS
    (
        SELECT 1 FROM [$(Schema)].[ShipmentEvents] WITH (HOLDLOCK)
        WHERE [TrackingNumber] = @Key COLLATE Latin1_General_100_BIN2
          AND DATALENGTH([TrackingNumber]) = DATALENGTH(@Key)
        HAVING COUNT_BIG(*) = @Version AND COALESCE(MAX([EventSequence]), 0) = @Version
           AND (@Version = 0 OR COUNT_BIG(CASE WHEN [EventSequence] = @Version AND [Status] = @ParentStatus
                AND [OccurredAt] = @ParentTime AND DATEPART(TZOFFSET, [OccurredAt]) = DATEPART(TZOFFSET, @ParentTime)
                THEN 1 END) = 1)
    )
        THROW 51011, 'Shipment history and projection disagree.', 1;

    DECLARE @NextVersion bigint = @ExpectedVersion + 1;
    INSERT INTO [$(Schema)].[ShipmentEvents] ([TrackingNumber], [EventSequence], [EventId], [Status], [OccurredAt], [SourceSystem])
    VALUES (@Key, @NextVersion, @EventId, @Status, @OccurredAt, @Source);
    IF @@ROWCOUNT <> 1 THROW 51011, 'Shipment append did not insert one event.', 1;

    UPDATE [$(Schema)].[Shipments]
    SET [Version] = @NextVersion, [Status] = @Status, [LastOccurredAt] = @OccurredAt
    WHERE [TrackingNumber] = @Key COLLATE Latin1_General_100_BIN2
      AND DATALENGTH([TrackingNumber]) = DATALENGTH(@Key) AND [Version] = @ExpectedVersion;
    IF @@ROWCOUNT <> 1 THROW 51011, 'Shipment projection update lost its expected version.', 1;

    SET @ResultCode = 'APPENDED';
END;
GO

INSERT INTO [$(Schema)].[SchemaVersion] ([Version], [MigrationId]) VALUES (1, 'M4.1');
