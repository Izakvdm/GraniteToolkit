/* ============================================================================
   GraniteWMS NiFi Import Framework
   01_Framework.sql

   Run this against the client's Granite database (select it in SSMS, or
   sqlcmd -d <GraniteDb>). There is no USE statement on purpose.

   Creates (idempotent, safe to re-run):
     Role   Custom_NiFiImport          - grant this to the NiFi SQL login
     Table  Custom_NiFiImportLog       - one row per file received, all feeds
     Proc   Custom_NiFi_ClearStaging   - housekeeping before each load
     Proc   Custom_NiFi_RunImport      - stamps staging, runs the feed proc,
                                         writes the log, returns one row
     Proc   Custom_NiFi_LogFailure     - logs a file that never reached staging

   How a feed plugs in (see _Template_Feed.sql):
     Table  Custom_<Feed>Staging       - columns = CSV header, plus StagingId
                                         and ImportId
     Proc   Custom_Import<Feed>        - the mapping / upsert, fixed OUTPUT
                                         parameter contract

   <Feed> is the name of the folder the CSV is dropped into, e.g.
   ...\Inbound\SalesOrder\orders.csv  ->  Feed = SalesOrder
   ========================================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------------------------------------------------------------------------
   Role
   ------------------------------------------------------------------------ */
IF DATABASE_PRINCIPAL_ID(N'Custom_NiFiImport') IS NULL
    CREATE ROLE Custom_NiFiImport AUTHORIZATION dbo;
GO

/* ---------------------------------------------------------------------------
   Import log (shared by every feed)
   ------------------------------------------------------------------------ */
IF OBJECT_ID(N'dbo.Custom_NiFiImportLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Custom_NiFiImportLog
    (
        Id             int            IDENTITY(1,1) NOT NULL,
        Feed           varchar(50)    NOT NULL,
        [FileName]     nvarchar(260)  NULL,
        [Status]       varchar(20)    NOT NULL,   -- STARTED / SUCCESS / ERROR
        [Message]      varchar(2000)  NULL,
        RowsInFile     int            NULL,
        RowsInserted   int            NULL,
        RowsUpdated    int            NULL,
        RowsUnchanged  int            NULL,
        ErrorNumber    int            NULL,       -- >= 50000 business rule
        StartDate      datetime2(0)   NOT NULL
            CONSTRAINT DF_Custom_NiFiImportLog_StartDate DEFAULT (SYSDATETIME()),
        FinishDate     datetime2(0)   NULL,
        FlowFileId     varchar(50)    NULL,       -- NiFi uuid, for provenance lookup
        CONSTRAINT PK_Custom_NiFiImportLog PRIMARY KEY CLUSTERED (Id)
    );

    CREATE NONCLUSTERED INDEX IX_Custom_NiFiImportLog_Feed_Start
        ON dbo.Custom_NiFiImportLog (Feed, StartDate DESC)
        INCLUDE ([Status]);
END;
GO

/* ---------------------------------------------------------------------------
   Custom_NiFi_ClearStaging
   Called by NiFi before every load. Removes rows that were never stamped
   (left behind by an interrupted load) and stamped rows from failed imports
   older than @KeepDays. Successful imports clear their own rows.
   Does nothing if the feed has no staging table; the load step reports that.
   ------------------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE dbo.Custom_NiFi_ClearStaging
    @Feed     varchar(50),
    @KeepDays int = 14
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    IF @Feed IS NULL OR @Feed = '' OR @Feed LIKE '%[^A-Za-z0-9_]%'
        RETURN;

    DECLARE @Staging nvarchar(300) = N'dbo.' + QUOTENAME(N'Custom_' + @Feed + N'Staging'),
            @Sql     nvarchar(max);

    IF OBJECT_ID(@Staging, N'U') IS NULL
        RETURN;

    SET @Sql = N'DELETE s FROM ' + @Staging + N' s
                 WHERE s.ImportId IS NULL
                    OR EXISTS (SELECT 1 FROM dbo.Custom_NiFiImportLog l
                               WHERE l.Id = s.ImportId
                                 AND l.StartDate < DATEADD(DAY, -@KeepDays, SYSDATETIME()));';

    EXEC sys.sp_executesql @Sql, N'@KeepDays int', @KeepDays = @KeepDays;
END;
GO

/* ---------------------------------------------------------------------------
   Custom_NiFi_RunImport
   The only proc NiFi calls after the CSV is in staging.

   1. Writes a STARTED row to the log.
   2. Stamps this file's staging rows with the log Id.
   3. Calls dbo.Custom_Import<Feed>.
   4. Clears the staging rows on SUCCESS (kept on ERROR for troubleshooting).
   5. Writes the outcome to the log and returns it as one row.

   Never throws. NiFi always gets one row back with Status SUCCESS or ERROR.
   NiFi processes one file at a time, so "unstamped rows" are always this
   file's rows.
   ------------------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE dbo.Custom_NiFi_RunImport
    @Feed       varchar(50),
    @FileName   nvarchar(260),
    @FlowFileId varchar(50) = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @LogId         int,
            @Status        varchar(20),
            @Message       varchar(2000),
            @RowsInFile    int = 0,
            @RowsInserted  int = 0,
            @RowsUpdated   int = 0,
            @RowsUnchanged int = 0,
            @ErrorNumber   int,
            @Proc          nvarchar(300),
            @Staging       nvarchar(300),
            @Err           varchar(2000),
            @Sql           nvarchar(max);

    INSERT INTO dbo.Custom_NiFiImportLog (Feed, [FileName], FlowFileId, [Status], [Message])
    VALUES (ISNULL(@Feed, ''), @FileName, @FlowFileId, 'STARTED', 'Import in progress');

    SET @LogId = SCOPE_IDENTITY();

    BEGIN TRY

        IF @Feed IS NULL OR @Feed = '' OR @Feed LIKE '%[^A-Za-z0-9_]%'
            THROW 50010, 'Feed name is blank or contains characters other than letters, digits and underscore.', 1;

        SET @Proc    = N'dbo.' + QUOTENAME(N'Custom_Import' + @Feed);
        SET @Staging = N'dbo.' + QUOTENAME(N'Custom_' + @Feed + N'Staging');

        IF OBJECT_ID(@Staging, N'U') IS NULL
        BEGIN
            SET @Err = CONCAT('Staging table ', @Staging, ' does not exist. Deploy the feed script for ', @Feed, '.');
            THROW 50011, @Err, 1;
        END;

        IF OBJECT_ID(@Proc, N'P') IS NULL
        BEGIN
            SET @Err = CONCAT('Import procedure ', @Proc, ' does not exist. Deploy the feed script for ', @Feed, '.');
            THROW 50012, @Err, 1;
        END;

        SET @Sql = N'UPDATE ' + @Staging + N' SET ImportId = @LogId WHERE ImportId IS NULL;';
        EXEC sys.sp_executesql @Sql, N'@LogId int', @LogId = @LogId;

        SET @Sql = N'EXEC ' + @Proc + N'
                        @ImportLogId   = @LogId,
                        @Status        = @Status        OUTPUT,
                        @Message       = @Message       OUTPUT,
                        @RowsInFile    = @RowsInFile    OUTPUT,
                        @RowsInserted  = @RowsInserted  OUTPUT,
                        @RowsUpdated   = @RowsUpdated   OUTPUT,
                        @RowsUnchanged = @RowsUnchanged OUTPUT,
                        @ErrorNumber   = @ErrorNumber   OUTPUT;';

        EXEC sys.sp_executesql @Sql,
             N'@LogId int, @Status varchar(20) OUTPUT, @Message varchar(2000) OUTPUT,
               @RowsInFile int OUTPUT, @RowsInserted int OUTPUT, @RowsUpdated int OUTPUT,
               @RowsUnchanged int OUTPUT, @ErrorNumber int OUTPUT',
             @LogId         = @LogId,
             @Status        = @Status        OUTPUT,
             @Message       = @Message       OUTPUT,
             @RowsInFile    = @RowsInFile    OUTPUT,
             @RowsInserted  = @RowsInserted  OUTPUT,
             @RowsUpdated   = @RowsUpdated   OUTPUT,
             @RowsUnchanged = @RowsUnchanged OUTPUT,
             @ErrorNumber   = @ErrorNumber   OUTPUT;

        IF @Status IS NULL OR @Status NOT IN ('SUCCESS', 'ERROR')
            SELECT @Status  = 'ERROR',
                   @Message = CONCAT('Import procedure ', @Proc,
                                     ' did not return a valid @Status. ', @Message);

    END TRY
    BEGIN CATCH

        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        SELECT @Status        = 'ERROR',
               @ErrorNumber   = ERROR_NUMBER(),
               @RowsInserted  = 0,
               @RowsUpdated   = 0,
               @RowsUnchanged = 0,
               @Message       = LEFT(
                   CASE WHEN ERROR_NUMBER() >= 50000
                        THEN ERROR_MESSAGE()
                        ELSE CONCAT('Unexpected error ', ERROR_NUMBER(),
                                    ' in ', ISNULL(ERROR_PROCEDURE(), 'RunImport'),
                                    ' line ', ERROR_LINE(), ': ', ERROR_MESSAGE())
                   END, 2000);

    END CATCH;

    -- Clear this import's staging rows on success. Failed imports keep their
    -- rows (stamped with the log Id) until ClearStaging ages them out.
    IF @Status = 'SUCCESS'
    BEGIN
        BEGIN TRY
            SET @Sql = N'DELETE FROM ' + @Staging + N' WHERE ImportId = @LogId;';
            EXEC sys.sp_executesql @Sql, N'@LogId int', @LogId = @LogId;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0
                ROLLBACK TRANSACTION;
        END CATCH;
    END;

    UPDATE dbo.Custom_NiFiImportLog
    SET [Status]      = @Status,
        [Message]     = LEFT(@Message, 2000),
        RowsInFile    = @RowsInFile,
        RowsInserted  = @RowsInserted,
        RowsUpdated   = @RowsUpdated,
        RowsUnchanged = @RowsUnchanged,
        ErrorNumber   = @ErrorNumber,
        FinishDate    = SYSDATETIME()
    WHERE Id = @LogId;

    SELECT @LogId         AS ImportLogId,
           @Status        AS [Status],
           @Message       AS [Message],
           @RowsInFile    AS RowsInFile,
           @RowsInserted  AS RowsInserted,
           @RowsUpdated   AS RowsUpdated,
           @RowsUnchanged AS RowsUnchanged,
           @ErrorNumber   AS ErrorNumber;
END;
GO

/* ---------------------------------------------------------------------------
   Custom_NiFi_LogFailure
   Called by NiFi when the CSV could not be loaded into staging (wrong
   columns, bad data types, missing staging table). Logs the file as ERROR
   and removes any partial, unstamped rows.
   ------------------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE dbo.Custom_NiFi_LogFailure
    @Feed       varchar(50),
    @FileName   nvarchar(260),
    @Message    nvarchar(max),
    @FlowFileId varchar(50) = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @LogId int;

    INSERT INTO dbo.Custom_NiFiImportLog
        (Feed, [FileName], FlowFileId, [Status], [Message], RowsInFile,
         RowsInserted, RowsUpdated, RowsUnchanged, ErrorNumber, FinishDate)
    VALUES
        (ISNULL(@Feed, ''), @FileName, @FlowFileId, 'ERROR',
         LEFT(CONCAT('The CSV could not be loaded into staging. Check the header and data types match Custom_',
                     @Feed, 'Staging. Detail: ', @Message), 2000),
         0, 0, 0, 0, 50020, SYSDATETIME());

    SET @LogId = SCOPE_IDENTITY();

    EXEC dbo.Custom_NiFi_ClearStaging @Feed = @Feed;

    SELECT @LogId AS ImportLogId;
END;
GO

GRANT EXECUTE ON dbo.Custom_NiFi_ClearStaging TO Custom_NiFiImport;
GRANT EXECUTE ON dbo.Custom_NiFi_RunImport    TO Custom_NiFiImport;
GRANT EXECUTE ON dbo.Custom_NiFi_LogFailure   TO Custom_NiFiImport;
GRANT SELECT  ON dbo.Custom_NiFiImportLog     TO Custom_NiFiImport;
GO
