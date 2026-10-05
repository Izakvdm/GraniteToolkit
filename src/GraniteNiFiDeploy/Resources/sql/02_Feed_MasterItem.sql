/* ============================================================================
   GraniteWMS NiFi Import Framework
   Feed: MasterItem

   Upsert of MasterItem on Code. Unchanged rows are not touched, every changed
   column is written to dbo.Audit, and a bad file is rejected in full.

   Drop CSV files in:  <ImportRoot>\Inbound\MasterItem\
   CSV header (any order, matched case-insensitively):
       Code, FormattedCode, Description, UOM, isActive

   Requires 01_Framework.sql. Run against the client's Granite database.
   Safe to re-run: the staging table is only created if missing and the
   proc is CREATE OR ALTER.

   Replaces the old dbo.[Custom_ImportMasterItems ] (plural, trailing space).
   Drop that object once this feed is live.
   ========================================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Staging only holds transient rows. A table from the older per-feed
-- design (no StagingId column) is rebuilt in the current shape.
IF OBJECT_ID(N'dbo.Custom_MasterItemStaging', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Custom_MasterItemStaging', N'StagingId') IS NULL
    DROP TABLE dbo.Custom_MasterItemStaging;
GO

IF OBJECT_ID(N'dbo.Custom_MasterItemStaging', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Custom_MasterItemStaging
    (
        StagingId          bigint IDENTITY(1,1) NOT NULL,   -- file order (not in CSV)
        Code               varchar(40)    NOT NULL,
        FormattedCode      varchar(40)    NOT NULL,
        [Description]      varchar(100)   NULL,
        UOM                varchar(10)    NULL,
        isActive           varchar(10)    NULL,             -- 1/0, true/false, Y/N
        ImportId           int            NULL,             -- set by RunImport (not in CSV)
        CONSTRAINT PK_Custom_MasterItemStaging PRIMARY KEY CLUSTERED (StagingId)
    );

    CREATE NONCLUSTERED INDEX IX_Custom_MasterItemStaging_ImportId ON dbo.Custom_MasterItemStaging (ImportId);
END;
GO

GRANT SELECT, INSERT ON dbo.Custom_MasterItemStaging TO Custom_NiFiImport;
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
CREATE OR ALTER PROCEDURE dbo.Custom_ImportMasterItem
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
    SET NOCOUNT ON;          -- keeps rowcount messages out of the result set
    SET XACT_ABORT ON;

    SELECT @Status = 'SUCCESS', @Message = NULL, @ErrorNumber = NULL,
           @RowsInFile = 0, @RowsInserted = 0, @RowsUpdated = 0, @RowsUnchanged = 0;

    DECLARE @Now         datetime     = GETDATE(),
            @AuditUser   varchar(20)  = 'ApacheNiFi',
            @Application varchar(50)  = 'CSV',
            @TableName   varchar(35)  = 'MasterItem',
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
            Code          varchar(40) COLLATE DATABASE_DEFAULT  NOT NULL,
            FormattedCode varchar(40) COLLATE DATABASE_DEFAULT  NOT NULL,
            [Description] varchar(100) COLLATE DATABASE_DEFAULT NULL,
            UOM           varchar(10) COLLATE DATABASE_DEFAULT  NULL,
            isActive      bit          NULL
        );

        INSERT INTO #Src (Code, FormattedCode, [Description], UOM, isActive)
        SELECT TRIM(s.Code),
               TRIM(s.FormattedCode),
               NULLIF(TRIM(s.[Description]), ''),
               NULLIF(TRIM(s.UOM), ''),
               CASE UPPER(TRIM(ISNULL(s.isActive, '')))
                    WHEN '1' THEN 1 WHEN 'TRUE'  THEN 1 WHEN 'Y' THEN 1 WHEN 'YES' THEN 1
                    WHEN '0' THEN 0 WHEN 'FALSE' THEN 0 WHEN 'N' THEN 0 WHEN 'NO'  THEN 0
               END
        FROM dbo.Custom_MasterItemStaging s
        WHERE s.ImportId = @ImportLogId;

        SET @Total = @@ROWCOUNT;

        -- An empty batch almost always means the NiFi wiring broke rather than
        -- that someone sent an empty file. Delete these three lines if you
        -- would rather a zero-row file be reported as a clean SUCCESS.
        IF @Total = 0
            THROW 50003, 'No staging rows were found for this ImportId. The file was empty or the CSV load did not run.', 1;

        /* ------------------------------------------------------------------
           2. Validate the whole batch before touching MasterItem.
              A bad master-data file is rejected in full rather than half
              applied - the CSV is the source of truth and should be re-sent.
           ------------------------------------------------------------------ */
        IF EXISTS (SELECT 1 FROM #Src WHERE Code = '' OR FormattedCode = '')
            SET @Err = 'File contains rows with a blank Code or FormattedCode.';

        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src WHERE isActive IS NULL)
            SELECT @Err = 'isActive must be 1/0, true/false or Y/N. Invalid on: '
                        + STRING_AGG(x.Code, ', ') WITHIN GROUP (ORDER BY x.Code)
            FROM (SELECT TOP (10) Code FROM #Src WHERE isActive IS NULL ORDER BY Code) x;

        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src GROUP BY Code HAVING COUNT(*) > 1)
            SELECT @Err = 'File contains duplicate Codes: '
                        + STRING_AGG(x.Code, ', ') WITHIN GROUP (ORDER BY x.Code)
            FROM (SELECT TOP (10) Code FROM #Src GROUP BY Code HAVING COUNT(*) > 1 ORDER BY Code) x;

        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src GROUP BY FormattedCode HAVING COUNT(*) > 1)
            SELECT @Err = 'File contains duplicate FormattedCodes: '
                        + STRING_AGG(x.FormattedCode, ', ') WITHIN GROUP (ORDER BY x.FormattedCode)
            FROM (SELECT TOP (10) FormattedCode FROM #Src GROUP BY FormattedCode HAVING COUNT(*) > 1 ORDER BY FormattedCode) x;

        -- FormattedCode is uniquely constrained independently of Code, so an
        -- incoming row can collide with a *different* existing item.
        IF @Err IS NULL AND EXISTS (SELECT 1
                                    FROM #Src s
                                    JOIN dbo.MasterItem mi ON mi.FormattedCode = s.FormattedCode
                                    WHERE mi.Code <> s.Code)
            SELECT @Err = 'FormattedCode already belongs to a different item: '
                        + STRING_AGG(x.Detail, ', ') WITHIN GROUP (ORDER BY x.Detail)
            FROM (SELECT TOP (10) s.FormattedCode + ' (existing Code ' + mi.Code + ')' AS Detail
                  FROM #Src s
                  JOIN dbo.MasterItem mi ON mi.FormattedCode = s.FormattedCode
                  WHERE mi.Code <> s.Code
                  ORDER BY s.FormattedCode) x;

        IF @Err IS NOT NULL
            THROW 50002, @Err, 1;

        /* ------------------------------------------------------------------
           3. Apply
           ------------------------------------------------------------------ */
        CREATE TABLE #Changed
        (
            RecordID         bigint       NOT NULL,
            NewVersion       int          NULL,
            OldCode          varchar(40) COLLATE DATABASE_DEFAULT  NULL, NewCode          varchar(40) COLLATE DATABASE_DEFAULT  NULL,
            OldFormattedCode varchar(40) COLLATE DATABASE_DEFAULT  NULL, NewFormattedCode varchar(40) COLLATE DATABASE_DEFAULT  NULL,
            OldDescription   varchar(100) COLLATE DATABASE_DEFAULT NULL, NewDescription   varchar(100) COLLATE DATABASE_DEFAULT NULL,
            OldUOM           varchar(10) COLLATE DATABASE_DEFAULT  NULL, NewUOM           varchar(10) COLLATE DATABASE_DEFAULT  NULL,
            OldIsActive      bit          NULL, NewIsActive      bit          NULL,
            ChangeType       varchar(10) COLLATE DATABASE_DEFAULT  NOT NULL
        );

        BEGIN TRANSACTION;

        /* -- 3a. Update items that already exist and actually differ.
              The NOT EXISTS/INTERSECT pair is a null-safe "is different" test,
              so unchanged rows are not touched and produce no audit noise. */
        UPDATE mi
        SET mi.FormattedCode = s.FormattedCode,
            mi.[Description] = s.[Description],
            mi.UOM           = s.UOM,
            mi.isActive      = s.isActive,
            mi.[Version]     = ISNULL(mi.[Version], 0) + 1,
            mi.AuditDate     = @Now,
            mi.AuditUser     = @AuditUser
        OUTPUT inserted.ID, inserted.[Version],
               deleted.Code,          inserted.Code,
               deleted.FormattedCode, inserted.FormattedCode,
               deleted.[Description], inserted.[Description],
               deleted.UOM,           inserted.UOM,
               deleted.isActive,      inserted.isActive,
               'UPDATE'
          INTO #Changed (RecordID, NewVersion,
                         OldCode, NewCode,
                         OldFormattedCode, NewFormattedCode,
                         OldDescription, NewDescription,
                         OldUOM, NewUOM,
                         OldIsActive, NewIsActive,
                         ChangeType)
        FROM dbo.MasterItem mi
        JOIN #Src s ON s.Code = mi.Code
        WHERE NOT EXISTS (
                  SELECT s.FormattedCode, s.[Description], s.UOM, s.isActive
                  INTERSECT
                  SELECT mi.FormattedCode, mi.[Description], mi.UOM, mi.isActive
              );

        SET @Updated = @@ROWCOUNT;

        /* -- 3b. Insert items that do not exist yet. */
        INSERT INTO dbo.MasterItem
            (Code, FormattedCode, [Description], UOM, isActive, [Version], AuditDate, AuditUser)
        OUTPUT inserted.ID, inserted.[Version],
               CONVERT(varchar(40),  NULL), inserted.Code,
               CONVERT(varchar(40),  NULL), inserted.FormattedCode,
               CONVERT(varchar(100), NULL), inserted.[Description],
               CONVERT(varchar(10),  NULL), inserted.UOM,
               CONVERT(bit,          NULL), inserted.isActive,
               'INSERT'
          INTO #Changed (RecordID, NewVersion,
                         OldCode, NewCode,
                         OldFormattedCode, NewFormattedCode,
                         OldDescription, NewDescription,
                         OldUOM, NewUOM,
                         OldIsActive, NewIsActive,
                         ChangeType)
        SELECT s.Code, s.FormattedCode, s.[Description], s.UOM, s.isActive, 1, @Now, @AuditUser
        FROM #Src s
        WHERE NOT EXISTS (SELECT 1 FROM dbo.MasterItem mi WHERE mi.Code = s.Code);

        SET @Inserted = @@ROWCOUNT;

        /* ------------------------------------------------------------------
           4. Audit - one row per column that actually changed.
              Every value is explicitly CONVERTed to varchar(max): the VALUES
              constructor resolves to the highest-precedence type in the list,
              so an unconverted bit would force 'Blue Widget' to be cast to bit.
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
            ('Code',          CONVERT(varchar(max), c.OldCode),          CONVERT(varchar(max), c.NewCode)),
            ('FormattedCode', CONVERT(varchar(max), c.OldFormattedCode), CONVERT(varchar(max), c.NewFormattedCode)),
            ('Description',   CONVERT(varchar(max), c.OldDescription),   CONVERT(varchar(max), c.NewDescription)),
            ('UOM',           CONVERT(varchar(max), c.OldUOM),           CONVERT(varchar(max), c.NewUOM)),
            ('isActive',      CONVERT(varchar(max), c.OldIsActive),      CONVERT(varchar(max), c.NewIsActive))
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
                        THEN ERROR_MESSAGE()   -- our own validation text, already readable
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

GRANT EXECUTE ON dbo.Custom_ImportMasterItem TO Custom_NiFiImport;
GO
