/* ============================================================================
   GraniteWMS NiFi Import Framework
   Feed: TradingPartner

   Upsert of TradingPartner on (Code, DocumentType). Only Description can change
   on an existing row, new rows are created active, changes go to dbo.Audit.

   Drop CSV files in:  <ImportRoot>\Inbound\TradingPartner\
   CSV header (any order, matched case-insensitively):
       Code, Description, DocumentType

   Requires 01_Framework.sql. Run against the client's Granite database.
   Safe to re-run: the staging table is only created if missing and the
   proc is CREATE OR ALTER.
   ========================================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Staging only holds transient rows. A table from the older per-feed
-- design (no StagingId column) is rebuilt in the current shape.
IF OBJECT_ID(N'dbo.Custom_TradingPartnerStaging', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Custom_TradingPartnerStaging', N'StagingId') IS NULL
    DROP TABLE dbo.Custom_TradingPartnerStaging;
GO

IF OBJECT_ID(N'dbo.Custom_TradingPartnerStaging', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Custom_TradingPartnerStaging
    (
        StagingId          bigint IDENTITY(1,1) NOT NULL,   -- file order (not in CSV)
        Code               varchar(50)    NOT NULL,
        [Description]      varchar(250)   NOT NULL,
        DocumentType       varchar(30)    NOT NULL,
        ImportId           int            NULL,             -- set by RunImport (not in CSV)
        CONSTRAINT PK_Custom_TradingPartnerStaging PRIMARY KEY CLUSTERED (StagingId)
    );

    CREATE NONCLUSTERED INDEX IX_Custom_TradingPartnerStaging_ImportId ON dbo.Custom_TradingPartnerStaging (ImportId);
END;
GO

GRANT SELECT, INSERT ON dbo.Custom_TradingPartnerStaging TO Custom_NiFiImport;
GO

/* ----------------------------------------------------------------------------
   Contract (same for every feed proc, called only by Custom_NiFi_RunImport):
     IN   @ImportLogId   - staging rows for this file carry ImportId = @ImportLogId
     OUT  @Status        - 'SUCCESS' or 'ERROR'
          @Message       - readable outcome, max 2000 chars
          @RowsInFile, @RowsInserted, @RowsUpdated, @RowsUnchanged
          @ErrorNumber   - NULL on success, >= 50000 business rule,
                           < 50000 unexpected SQL Server error
   Never throws for data problems, never touches the log, never clears staging.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.Custom_ImportTradingPartner
    @ImportLogId   int,
    @Status        varchar(20)   OUTPUT,
    @Message       varchar(2000) OUTPUT,
    @RowsInFile    int           OUTPUT,
    @RowsInserted  int           OUTPUT,
    @RowsUpdated   int           OUTPUT,
    @RowsUnchanged int           OUTPUT,
    @ErrorNumber   int           OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SELECT @Status = 'SUCCESS', @Message = NULL, @ErrorNumber = NULL,
           @RowsInFile = 0, @RowsInserted = 0, @RowsUpdated = 0, @RowsUnchanged = 0;

    DECLARE @Now         datetime      = GETDATE(),
            @AuditUser   varchar(20)   = 'ApacheNiFi',
            @Application varchar(50)   = 'CSV',
            @TableName   varchar(35)   = 'TradingPartner',
            @Total       int = 0,
            @Inserted    int = 0,
            @Updated     int = 0,
            @Unchanged   int = 0,
            @Err         varchar(2000);

    BEGIN TRY

        IF @ImportLogId IS NULL
            THROW 50001, 'No @ImportLogId was supplied. Call this through dbo.Custom_NiFi_RunImport.', 1;

        /* ------------------------------------------------------------------
           1. Snapshot + normalise this import's staging rows
           ------------------------------------------------------------------ */
        CREATE TABLE #Src
        (
            Code          varchar(50) COLLATE DATABASE_DEFAULT  NOT NULL,
            [Description] varchar(250) COLLATE DATABASE_DEFAULT NOT NULL,
            DocumentType  varchar(30) COLLATE DATABASE_DEFAULT  NOT NULL
        );

        INSERT INTO #Src (Code, [Description], DocumentType)
        SELECT TRIM(s.Code),
               TRIM(s.[Description]),
               TRIM(s.DocumentType)
        FROM dbo.Custom_TradingPartnerStaging s
        WHERE s.ImportId = @ImportLogId;

        SET @Total = @@ROWCOUNT;

        -- An empty batch almost always means the NiFi wiring broke rather than
        -- that someone sent an empty file. Delete these two lines if you would
        -- rather a zero-row file be reported as a clean SUCCESS.
        IF @Total = 0
            THROW 50003, 'No staging rows were found for this ImportId. The file was empty or the CSV load did not run.', 1;

        /* ------------------------------------------------------------------
           2. Validate the whole batch before touching TradingPartner.
           ------------------------------------------------------------------ */
        IF EXISTS (SELECT 1 FROM #Src WHERE Code = '' OR [Description] = '' OR DocumentType = '')
            SET @Err = 'File contains rows with a blank Code, Description or DocumentType.';

        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src GROUP BY Code, DocumentType HAVING COUNT(*) > 1)
            SELECT @Err = 'File contains duplicate Code/DocumentType pairs: '
                        + STRING_AGG(x.Detail, ', ') WITHIN GROUP (ORDER BY x.Detail)
            FROM (SELECT TOP (10) Code + ' / ' + DocumentType AS Detail
                  FROM #Src
                  GROUP BY Code, DocumentType
                  HAVING COUNT(*) > 1
                  ORDER BY Code + ' / ' + DocumentType) x;

        -- Pre-existing duplicates in the target would make the UPDATE below hit
        -- several rows for one incoming record. Caught here rather than silently
        -- fanning out. The unique index in the prerequisites prevents this.
        IF @Err IS NULL AND EXISTS (SELECT 1
                                    FROM dbo.TradingPartner tp
                                    JOIN #Src s ON s.Code = tp.Code AND s.DocumentType = tp.DocumentType
                                    GROUP BY tp.Code, tp.DocumentType
                                    HAVING COUNT(*) > 1)
            SELECT @Err = 'TradingPartner already holds duplicate rows for: '
                        + STRING_AGG(x.Detail, ', ') WITHIN GROUP (ORDER BY x.Detail)
            FROM (SELECT TOP (10) tp.Code + ' / ' + tp.DocumentType AS Detail
                  FROM dbo.TradingPartner tp
                  JOIN #Src s ON s.Code = tp.Code AND s.DocumentType = tp.DocumentType
                  GROUP BY tp.Code, tp.DocumentType
                  HAVING COUNT(*) > 1
                  ORDER BY tp.Code + ' / ' + tp.DocumentType) x;

        IF @Err IS NOT NULL
            THROW 50002, @Err, 1;

        /* ------------------------------------------------------------------
           3. Apply
           ------------------------------------------------------------------ */
        CREATE TABLE #Changed
        (
            RecordID        bigint       NOT NULL,
            NewVersion      smallint     NULL,
            OldCode         varchar(50) COLLATE DATABASE_DEFAULT  NULL, NewCode         varchar(50) COLLATE DATABASE_DEFAULT  NULL,
            OldDescription  varchar(250) COLLATE DATABASE_DEFAULT NULL, NewDescription  varchar(250) COLLATE DATABASE_DEFAULT NULL,
            OldDocumentType varchar(30) COLLATE DATABASE_DEFAULT  NULL, NewDocumentType varchar(30) COLLATE DATABASE_DEFAULT  NULL,
            OldIsActive     bit          NULL, NewIsActive     bit          NULL,
            ChangeType      varchar(10) COLLATE DATABASE_DEFAULT  NOT NULL
        );

        BEGIN TRANSACTION;

        /* -- 3a. Update existing partners whose Description actually differs.
              Code and DocumentType are the match key, so they cannot change
              here. If you switch the key to Code alone, add
                  tp.DocumentType = s.DocumentType
              to the SET list and add DocumentType to the NOT EXISTS/INTERSECT
              comparison below - the audit block already handles it.

              Version is smallint; the CASE stops an arithmetic overflow from
              failing the whole import once a row passes 32767 updates.        */
        UPDATE tp
        SET tp.[Description] = s.[Description],
            tp.[Version]     = CASE WHEN ISNULL(tp.[Version], 0) >= 32767
                                    THEN 32767
                                    ELSE ISNULL(tp.[Version], 0) + 1 END,
            tp.AuditDate     = @Now,
            tp.AuditUser     = @AuditUser
        OUTPUT inserted.ID, inserted.[Version],
               deleted.Code,          inserted.Code,
               deleted.[Description], inserted.[Description],
               deleted.DocumentType,  inserted.DocumentType,
               deleted.isActive,      inserted.isActive,
               'UPDATE'
          INTO #Changed (RecordID, NewVersion,
                         OldCode, NewCode,
                         OldDescription, NewDescription,
                         OldDocumentType, NewDocumentType,
                         OldIsActive, NewIsActive,
                         ChangeType)
        FROM dbo.TradingPartner tp
        JOIN #Src s
          ON s.Code         = tp.Code
         AND s.DocumentType = tp.DocumentType
        WHERE NOT EXISTS (
                  SELECT s.[Description]
                  INTERSECT
                  SELECT tp.[Description]
              );

        SET @Updated = @@ROWCOUNT;

        /* -- 3b. Insert partners that do not exist yet.
              isActive is not carried by the CSV, so new rows default to active.
              ContactPerson, Tel, Email, addresses and ERPIdentification are
              left NULL for a later feed or manual capture.                    */
        INSERT INTO dbo.TradingPartner
            (Code, [Description], DocumentType, isActive, [Version], AuditDate, AuditUser)
        OUTPUT inserted.ID, inserted.[Version],
               CONVERT(varchar(50),  NULL), inserted.Code,
               CONVERT(varchar(250), NULL), inserted.[Description],
               CONVERT(varchar(30),  NULL), inserted.DocumentType,
               CONVERT(bit,          NULL), inserted.isActive,
               'INSERT'
          INTO #Changed (RecordID, NewVersion,
                         OldCode, NewCode,
                         OldDescription, NewDescription,
                         OldDocumentType, NewDocumentType,
                         OldIsActive, NewIsActive,
                         ChangeType)
        SELECT s.Code, s.[Description], s.DocumentType, 1, 1, @Now, @AuditUser
        FROM #Src s
        WHERE NOT EXISTS (SELECT 1
                          FROM dbo.TradingPartner tp
                          WHERE tp.Code         = s.Code
                            AND tp.DocumentType = s.DocumentType);

        SET @Inserted = @@ROWCOUNT;

        /* ------------------------------------------------------------------
           4. Audit - one row per column that actually changed.
              Every value is explicitly CONVERTed to varchar(max): the VALUES
              constructor resolves to the highest-precedence type in the list,
              so an unconverted bit would force the text columns to be cast
              to bit.
           ------------------------------------------------------------------ */
        INSERT INTO dbo.Audit
            (AuditDate, AuditTime, [User], RecordID, [Application],
             TableName, ChangeType, ColumnName, PreviousValue, NewValue, RecordVersion)
        SELECT CAST(@Now AS date),
               CAST(@Now AS time),
               @AuditUser,
               c.RecordID,
               @Application,
               @TableName,
               c.ChangeType,
               v.ColumnName,
               v.PreviousValue,
               v.NewValue,
               c.NewVersion
        FROM #Changed c
        CROSS APPLY (VALUES
            ('Code',         CONVERT(varchar(max), c.OldCode),         CONVERT(varchar(max), c.NewCode)),
            ('Description',  CONVERT(varchar(max), c.OldDescription),  CONVERT(varchar(max), c.NewDescription)),
            ('DocumentType', CONVERT(varchar(max), c.OldDocumentType), CONVERT(varchar(max), c.NewDocumentType)),
            ('isActive',     CONVERT(varchar(max), c.OldIsActive),     CONVERT(varchar(max), c.NewIsActive))
        ) v (ColumnName, PreviousValue, NewValue)
        -- null-safe inequality: identical values (including NULL = NULL) are skipped
        WHERE EXISTS (SELECT v.PreviousValue EXCEPT SELECT v.NewValue);

        COMMIT TRANSACTION;

        SET @Unchanged = @Total - @Inserted - @Updated;
        SET @Message   = CONCAT(@Total, ' row(s) in file: ',
                                @Inserted, ' inserted, ',
                                @Updated, ' updated, ',
                                @Unchanged, ' unchanged');

    END TRY
    BEGIN CATCH

        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        -- Nothing was applied, so report zeroes rather than partial counts.
        SELECT @Status      = 'ERROR',
               @ErrorNumber = ERROR_NUMBER(),
               @Inserted    = 0,
               @Updated     = 0,
               @Unchanged   = 0,
               @Message     = LEFT(
                   CASE WHEN ERROR_NUMBER() >= 50000
                        THEN ERROR_MESSAGE()
                        ELSE CONCAT('Unexpected error ', ERROR_NUMBER(),
                                    ' at line ', ERROR_LINE(), ': ', ERROR_MESSAGE())
                   END, 2000);

    END CATCH;

    SELECT @RowsInFile    = @Total,
           @RowsInserted  = @Inserted,
           @RowsUpdated   = @Updated,
           @RowsUnchanged = @Unchanged;
END;
GO

GRANT EXECUTE ON dbo.Custom_ImportTradingPartner TO Custom_NiFiImport;
GO
