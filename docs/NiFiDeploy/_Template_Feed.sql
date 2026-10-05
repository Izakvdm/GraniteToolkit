/* ============================================================================
   GraniteWMS NiFi Import Framework
   TEMPLATE for a new feed

   1. Copy this file to NN_Feed_<Feed>.sql and replace every  ZZFeed  with the
      feed name. The feed name is the Inbound sub-folder name, letters, digits
      and underscore only (e.g. WorksOrder, StockOnHand).
   2. Make the staging columns match the CSV header exactly (names, not order).
      Keep StagingId and ImportId; they are not in the CSV.
   3. Write the mapping in section 3. Keep the OUTPUT parameter contract.
   4. Run the script against the Granite database.
   5. Create the folder <ImportRoot>\Inbound\ZZFeed\ and drop a test file.
      No NiFi change is needed.
   ========================================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Staging only holds transient rows. A table from the older per-feed
-- design (no StagingId column) is rebuilt in the current shape.
IF OBJECT_ID(N'dbo.Custom_ZZFeedStaging', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Custom_ZZFeedStaging', N'StagingId') IS NULL
    DROP TABLE dbo.Custom_ZZFeedStaging;
GO

IF OBJECT_ID(N'dbo.Custom_ZZFeedStaging', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Custom_ZZFeedStaging
    (
        StagingId          bigint IDENTITY(1,1) NOT NULL,   -- file order (not in CSV)
        -- >>> CSV columns, same names as the CSV header <<<
        Code               varchar(40)    NOT NULL,
        Qty                decimal(19,4)  NOT NULL,
        -- >>> end CSV columns <<<
        ImportId           int            NULL,             -- set by RunImport (not in CSV)
        CONSTRAINT PK_Custom_ZZFeedStaging PRIMARY KEY CLUSTERED (StagingId)
    );

    CREATE NONCLUSTERED INDEX IX_Custom_ZZFeedStaging_ImportId ON dbo.Custom_ZZFeedStaging (ImportId);
END;
GO

GRANT SELECT, INSERT ON dbo.Custom_ZZFeedStaging TO Custom_NiFiImport;
GO

CREATE OR ALTER PROCEDURE dbo.Custom_ImportZZFeed
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

    DECLARE @Now       datetime      = GETDATE(),
            @AuditUser varchar(20)   = 'ApacheNiFi',
            @Err       varchar(2000);

    BEGIN TRY

        /* 1. Snapshot this file's rows ------------------------------------ */
        SELECT TRIM(s.Code) AS Code,
               s.Qty,
               s.StagingId
        INTO #Src
        FROM dbo.Custom_ZZFeedStaging s
        WHERE s.ImportId = @ImportLogId;

        SET @RowsInFile = @@ROWCOUNT;

        IF @RowsInFile = 0
            THROW 50003, 'No staging rows were found for this import. The file was empty.', 1;

        /* 2. Validate the whole file before writing anything -------------- */
        IF EXISTS (SELECT 1 FROM #Src WHERE Code = '')
            SET @Err = 'File contains rows with a blank Code.';

        -- add further checks here, each guarded by  IF @Err IS NULL AND ...

        IF @Err IS NOT NULL
            THROW 50002, @Err, 1;

        /* 3. Apply ----------------------------------------------------------- */
        BEGIN TRANSACTION;

        -- UPDATE ... FROM dbo.<Target> JOIN #Src ...   SET @RowsUpdated  = @@ROWCOUNT;
        -- INSERT INTO dbo.<Target> ... SELECT ... FROM #Src WHERE NOT EXISTS (...)
        --                                               SET @RowsInserted = @@ROWCOUNT;
        -- INSERT INTO dbo.Audit (...)   -- see 02_Feed_MasterItem.sql for the pattern

        COMMIT TRANSACTION;

        SET @RowsUnchanged = @RowsInFile - @RowsInserted - @RowsUpdated;
        SET @Message = CONCAT(@RowsInFile, ' row(s) in file: ', @RowsInserted, ' inserted, ',
                              @RowsUpdated, ' updated, ', @RowsUnchanged, ' unchanged');

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
                                    ' at line ', ERROR_LINE(), ': ', ERROR_MESSAGE())
                   END, 2000);

    END CATCH;
END;
GO

GRANT EXECUTE ON dbo.Custom_ImportZZFeed TO Custom_NiFiImport;
GO
