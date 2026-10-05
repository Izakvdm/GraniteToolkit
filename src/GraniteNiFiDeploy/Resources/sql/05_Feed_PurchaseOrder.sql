/* ============================================================================
   GraniteWMS NiFi Import Framework
   Feed: PurchaseOrder

   Insert-only. Same rules as SalesOrder, creating RECEIVING documents.

   Drop CSV files in:  <ImportRoot>\Inbound\PurchaseOrder\
   CSV header (any order, matched case-insensitively):
       OrderNumber, TradingPartnerCode, ItemCode, Qty, UOM

   Requires 01_Framework.sql. Run against the client's Granite database.
   Safe to re-run: the staging table is only created if missing and the
   proc is CREATE OR ALTER.

   BEFORE THE FIRST RUN confirm the @DocumentType, @DocumentStatus and @Site
   defaults on the procedure against live data:
       SELECT DISTINCT [Type], [Status], Site FROM dbo.Document;
   Wrong values import cleanly but the documents never show in WebDesktop.
   ========================================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Staging only holds transient rows. A table from the older per-feed
-- design (no StagingId column) is rebuilt in the current shape.
IF OBJECT_ID(N'dbo.Custom_PurchaseOrderStaging', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Custom_PurchaseOrderStaging', N'StagingId') IS NULL
    DROP TABLE dbo.Custom_PurchaseOrderStaging;
GO

IF OBJECT_ID(N'dbo.Custom_PurchaseOrderStaging', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Custom_PurchaseOrderStaging
    (
        StagingId          bigint IDENTITY(1,1) NOT NULL,   -- file order (not in CSV)
        OrderNumber        varchar(50)    NOT NULL,
        TradingPartnerCode varchar(50)    NOT NULL,
        ItemCode           varchar(40)    NOT NULL,
        Qty                decimal(19,4)  NOT NULL,
        UOM                varchar(20)    NOT NULL,
        ImportId           int            NULL,             -- set by RunImport (not in CSV)
        CONSTRAINT PK_Custom_PurchaseOrderStaging PRIMARY KEY CLUSTERED (StagingId)
    );

    CREATE NONCLUSTERED INDEX IX_Custom_PurchaseOrderStaging_ImportId ON dbo.Custom_PurchaseOrderStaging (ImportId);
END;
GO

GRANT SELECT, INSERT ON dbo.Custom_PurchaseOrderStaging TO Custom_NiFiImport;
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
CREATE OR ALTER PROCEDURE dbo.Custom_ImportPurchaseOrder
    @ImportLogId           int,
    @Status                varchar(20)   OUTPUT,
    @Message               varchar(2000) OUTPUT,
    @RowsInFile            int           OUTPUT,
    @RowsInserted          int           OUTPUT,
    @RowsUpdated           int           OUTPUT,
    @RowsUnchanged         int           OUTPUT,
    @ErrorNumber           int           OUTPUT,
    @DocumentType          varchar(30) = 'RECEIVING',   -- confirm per client
    @DocumentStatus        varchar(30) = 'ENTERED',     -- confirm per client
    @Site                  varchar(30) = '',            -- confirm per client
    @PartnerDocumentType   varchar(30) = 'RECEIVING'    -- TradingPartner row used for the
                                                        -- description; NULL = any
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SELECT @Status = 'SUCCESS', @Message = NULL, @ErrorNumber = NULL,
           @RowsInFile = 0, @RowsInserted = 0, @RowsUpdated = 0, @RowsUnchanged = 0;

    DECLARE @Now         datetime      = GETDATE(),
            @AuditUser   varchar(20)   = 'ApacheNiFi',
            @Application varchar(50)   = 'CSV',
            @Total       int = 0,
            @Orders      int = 0,
            @Lines       int = 0,
            @Err         varchar(2000);

    BEGIN TRY

        IF @ImportLogId IS NULL
            THROW 50001, 'No @ImportLogId was supplied. Call this through dbo.Custom_NiFi_RunImport.', 1;

        /* ------------------------------------------------------------------
           1. Snapshot + normalise this import's staging rows.
              LineNumber follows StagingId (staging identity), so the line
              order in the file is the line order on the document.
           ------------------------------------------------------------------ */
        CREATE TABLE #Src
        (
            OrderNumber        varchar(50) COLLATE DATABASE_DEFAULT    NOT NULL,
            TradingPartnerCode varchar(50) COLLATE DATABASE_DEFAULT    NOT NULL,
            ItemCode           varchar(40) COLLATE DATABASE_DEFAULT    NOT NULL,
            Qty                decimal(19, 4) NOT NULL,
            UOM                varchar(20) COLLATE DATABASE_DEFAULT    NOT NULL,
            StagingId          bigint         NOT NULL,
            LineNumber         int            NULL
        );

        INSERT INTO #Src (OrderNumber, TradingPartnerCode, ItemCode, Qty, UOM, StagingId)
        SELECT 
               TRIM(s.OrderNumber),
               TRIM(s.TradingPartnerCode),
               TRIM(s.ItemCode),
               s.Qty,
               TRIM(s.UOM),
               s.StagingId
        FROM dbo.Custom_PurchaseOrderStaging s
        WHERE s.ImportId = @ImportLogId;

        SET @Total = @@ROWCOUNT;

        IF @Total = 0
            THROW 50003, 'No staging rows were found for this ImportId. The file was empty or the CSV load did not run.', 1;

        UPDATE x
        SET x.LineNumber = x.rn
        FROM (SELECT LineNumber,
                     ROW_NUMBER() OVER (PARTITION BY OrderNumber ORDER BY StagingId) AS rn
              FROM #Src) x;

        /* ------------------------------------------------------------------
           2. Validate the whole batch before writing anything.
           ------------------------------------------------------------------ */

        -- 2a. Blanks and non-positive quantities
        IF EXISTS (SELECT 1 FROM #Src
                   WHERE OrderNumber = '' OR TradingPartnerCode = ''
                      OR ItemCode = '' OR UOM = '')
            SET @Err = 'File contains rows with a blank OrderNumber, TradingPartnerCode, ItemCode or UOM.';

        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src WHERE Qty <= 0)
            SELECT @Err = 'File contains lines with a quantity of zero or less: '
                        + STRING_AGG(x.Detail, ', ') WITHIN GROUP (ORDER BY x.Detail)
            FROM (SELECT TOP (10) OrderNumber + ' / ' + ItemCode AS Detail
                  FROM #Src WHERE Qty <= 0 ORDER BY OrderNumber, ItemCode) x;

        -- 2b. Width traps. Staging is wider than the target in two places, so
        --     without this the import fails mid-transaction or silently cuts.
        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src WHERE LEN(OrderNumber) > 30)
            SELECT @Err = 'OrderNumber exceeds the 30 characters Document.Number allows: '
                        + STRING_AGG(x.OrderNumber, ', ') WITHIN GROUP (ORDER BY x.OrderNumber)
            FROM (SELECT DISTINCT TOP (10) OrderNumber FROM #Src WHERE LEN(OrderNumber) > 30 ORDER BY OrderNumber) x;

        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src WHERE LEN(TradingPartnerCode) > 20)
            SELECT @Err = 'TradingPartnerCode exceeds the 20 characters Document.TradingPartnerCode allows: '
                        + STRING_AGG(x.TradingPartnerCode, ', ') WITHIN GROUP (ORDER BY x.TradingPartnerCode)
            FROM (SELECT DISTINCT TOP (10) TradingPartnerCode FROM #Src WHERE LEN(TradingPartnerCode) > 20 ORDER BY TradingPartnerCode) x;

        -- 2c. One trading partner per order
        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src
                                    GROUP BY OrderNumber
                                    HAVING COUNT(DISTINCT TradingPartnerCode) > 1)
            SELECT @Err = 'These orders carry more than one TradingPartnerCode across their lines: '
                        + STRING_AGG(x.OrderNumber, ', ') WITHIN GROUP (ORDER BY x.OrderNumber)
            FROM (SELECT TOP (10) OrderNumber FROM #Src
                  GROUP BY OrderNumber HAVING COUNT(DISTINCT TradingPartnerCode) > 1
                  ORDER BY OrderNumber) x;

        -- 2d. Repeated item on one order. The CSV has no column that would
        --     distinguish two such lines, so this is treated as a file error
        --     rather than two legitimate lines. Remove this block if your
        --     source genuinely sends split lines.
        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src
                                    GROUP BY OrderNumber, ItemCode, UOM
                                    HAVING COUNT(*) > 1)
            SELECT @Err = 'These orders repeat the same item and UOM on more than one line: '
                        + STRING_AGG(x.Detail, ', ') WITHIN GROUP (ORDER BY x.Detail)
            FROM (SELECT TOP (10) OrderNumber + ' / ' + ItemCode AS Detail
                  FROM #Src GROUP BY OrderNumber, ItemCode, UOM
                  HAVING COUNT(*) > 1 ORDER BY OrderNumber + ' / ' + ItemCode) x;

        -- 2e. Orders that already exist. Insert-only policy: reject the batch.
        IF @Err IS NULL AND EXISTS (SELECT 1
                                    FROM dbo.[Document] d
                                    JOIN #Src s ON s.OrderNumber = d.Number)
            SELECT @Err = 'These orders already exist in Document and will not be overwritten: '
                        + STRING_AGG(x.Number, ', ') WITHIN GROUP (ORDER BY x.Number)
            FROM (SELECT DISTINCT TOP (10) d.Number
                  FROM dbo.[Document] d JOIN #Src s ON s.OrderNumber = d.Number
                  ORDER BY d.Number) x;

        -- 2f. Unknown items. Item_id is NOT NULL with a foreign key, so an
        --     unmatched code cannot be imported at all.
        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src s
                                    WHERE NOT EXISTS (SELECT 1 FROM dbo.MasterItem mi WHERE mi.Code = s.ItemCode))
            SELECT @Err = 'These item codes do not exist in MasterItem: '
                        + STRING_AGG(x.ItemCode, ', ') WITHIN GROUP (ORDER BY x.ItemCode)
            FROM (SELECT DISTINCT TOP (10) s.ItemCode FROM #Src s
                  WHERE NOT EXISTS (SELECT 1 FROM dbo.MasterItem mi WHERE mi.Code = s.ItemCode)
                  ORDER BY s.ItemCode) x;

        -- 2g. Unknown trading partners. Document.TradingPartnerCode is
        --     nullable, so this check is a choice rather than a constraint -
        --     drop this block if orders for unknown partners are acceptable.
        IF @Err IS NULL AND EXISTS (SELECT 1 FROM #Src s
                                    WHERE NOT EXISTS (SELECT 1 FROM dbo.TradingPartner tp WHERE tp.Code = s.TradingPartnerCode))
            SELECT @Err = 'These trading partner codes do not exist in TradingPartner: '
                        + STRING_AGG(x.TradingPartnerCode, ', ') WITHIN GROUP (ORDER BY x.TradingPartnerCode)
            FROM (SELECT DISTINCT TOP (10) s.TradingPartnerCode FROM #Src s
                  WHERE NOT EXISTS (SELECT 1 FROM dbo.TradingPartner tp WHERE tp.Code = s.TradingPartnerCode)
                  ORDER BY s.TradingPartnerCode) x;

        IF @Err IS NOT NULL
            THROW 50002, @Err, 1;

        /* ------------------------------------------------------------------
           3. Apply - header first, then lines against the new Document ids.
           ------------------------------------------------------------------ */
        CREATE TABLE #NewDocuments
        (
            DocumentID bigint      NOT NULL,
            Number     varchar(30) COLLATE DATABASE_DEFAULT NOT NULL
        );

        CREATE TABLE #NewLines
        (
            DetailID   bigint         NOT NULL,
            DocumentID bigint         NOT NULL,
            LineNumber varchar(50) COLLATE DATABASE_DEFAULT    NULL,
            ItemCode   varchar(40) COLLATE DATABASE_DEFAULT    NULL,
            Qty        decimal(19, 4) NULL,
            UOM        varchar(20) COLLATE DATABASE_DEFAULT    NULL
        );

        BEGIN TRANSACTION;

        /* -- 3a. One Document per distinct OrderNumber.
              TradingPartnerDescription is denormalised onto the header, so it
              is resolved once here rather than per line.                      */
        INSERT INTO dbo.[Document]
            (Number, [Type], [Status], TradingPartnerCode, TradingPartnerDescription,
             CreateDate, isActive, Site, [Version], AuditDate, AuditUser)
        OUTPUT inserted.ID, inserted.Number
          INTO #NewDocuments (DocumentID, Number)
        SELECT h.OrderNumber,
               @DocumentType,
               @DocumentStatus,
               h.TradingPartnerCode,
               tp.[Description],
               @Now,
               1,
               @Site,
               1,
               @Now,
               @AuditUser
        FROM (SELECT OrderNumber, MIN(TradingPartnerCode) AS TradingPartnerCode
              FROM #Src
              GROUP BY OrderNumber) h
        OUTER APPLY (SELECT TOP (1) t.[Description]
                     FROM dbo.TradingPartner t
                     WHERE t.Code = h.TradingPartnerCode
                       AND (@PartnerDocumentType IS NULL OR t.DocumentType = @PartnerDocumentType)
                     ORDER BY t.ID) tp;

        SET @Orders = @@ROWCOUNT;

        /* -- 3b. Lines, joined back to the headers just created.             */
        INSERT INTO dbo.DocumentDetail
            (Document_id, Item_id, LineNumber, Qty, UOM,
             Completed, Cancelled, [Status], [Type],
             [Version], AuditDate, AuditUser)
        OUTPUT inserted.ID, inserted.Document_id, inserted.LineNumber,
               NULL, inserted.Qty, inserted.UOM
          INTO #NewLines (DetailID, DocumentID, LineNumber, ItemCode, Qty, UOM)
        SELECT nd.DocumentID,
               mi.ID,
               CAST(s.LineNumber AS varchar(50)),
               s.Qty,
               s.UOM,
               0,
               0,
               @DocumentStatus,
               @DocumentType,
               1,
               @Now,
               @AuditUser
        FROM #Src s
        JOIN #NewDocuments nd ON nd.Number  = s.OrderNumber
        JOIN dbo.MasterItem mi ON mi.Code   = s.ItemCode;

        SET @Lines = @@ROWCOUNT;

        -- OUTPUT cannot reach the source alias, so ItemCode is filled in after.
        UPDATE nl
        SET nl.ItemCode = s.ItemCode
        FROM #NewLines nl
        JOIN #NewDocuments nd ON nd.DocumentID = nl.DocumentID
        JOIN #Src s ON s.OrderNumber = nd.Number
                   AND CAST(s.LineNumber AS varchar(50)) = nl.LineNumber;

        /* ------------------------------------------------------------------
           4. Audit - one row per populated column, matching the shape the
              MasterItem and TradingPartner imports write.

              Volume note: this is roughly five rows per header plus four per
              line. On a high-volume order feed that adds up fast. If it
              becomes a problem, cut the detail block to a single row per line
              rather than changing the header block - the header is where the
              audit value is.
           ------------------------------------------------------------------ */
        INSERT INTO dbo.Audit
            (AuditDate, AuditTime, [User], RecordID, [Application],
             TableName, ChangeType, ColumnName, PreviousValue, NewValue, RecordVersion)
        SELECT CAST(@Now AS date), CAST(@Now AS time), @AuditUser,
               d.DocumentID, @Application, 'Document', 'INSERT',
               v.ColumnName, NULL, v.NewValue, 1
        FROM #NewDocuments d
        JOIN (SELECT OrderNumber, MIN(TradingPartnerCode) AS TradingPartnerCode
              FROM #Src GROUP BY OrderNumber) h ON h.OrderNumber = d.Number
        CROSS APPLY (VALUES
            ('Number',             CONVERT(varchar(max), d.Number)),
            ('Type',               CONVERT(varchar(max), @DocumentType)),
            ('Status',             CONVERT(varchar(max), @DocumentStatus)),
            ('TradingPartnerCode', CONVERT(varchar(max), h.TradingPartnerCode)),
            ('Site',              CONVERT(varchar(max), @Site))
        ) v (ColumnName, NewValue)
        WHERE v.NewValue IS NOT NULL;

        INSERT INTO dbo.Audit
            (AuditDate, AuditTime, [User], RecordID, [Application],
             TableName, ChangeType, ColumnName, PreviousValue, NewValue, RecordVersion)
        SELECT CAST(@Now AS date), CAST(@Now AS time), @AuditUser,
               l.DetailID, @Application, 'DocumentDetail', 'INSERT',
               v.ColumnName, NULL, v.NewValue, 1
        FROM #NewLines l
        CROSS APPLY (VALUES
            ('LineNumber', CONVERT(varchar(max), l.LineNumber)),
            ('ItemCode',   CONVERT(varchar(max), l.ItemCode)),
            ('Qty',        CONVERT(varchar(max), l.Qty)),
            ('UOM',        CONVERT(varchar(max), l.UOM))
        ) v (ColumnName, NewValue)
        WHERE v.NewValue IS NOT NULL;

        COMMIT TRANSACTION;

        SET @Message = CONCAT(@Total, ' row(s) in file: ',
                              @Orders, ' order(s) and ',
                              @Lines, ' line(s) created');

    END TRY
    BEGIN CATCH

        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        SELECT @Status      = 'ERROR',
               @ErrorNumber = ERROR_NUMBER(),
               @Orders      = 0,
               @Lines       = 0,
               @Message     = LEFT(
                   CASE WHEN ERROR_NUMBER() >= 50000
                        THEN ERROR_MESSAGE()
                        ELSE CONCAT('Unexpected error ', ERROR_NUMBER(),
                                    ' at line ', ERROR_LINE(), ': ', ERROR_MESSAGE())
                   END, 2000);

    END CATCH;

    SELECT @RowsInFile    = @Total,
           @RowsInserted  = @Lines,
           @RowsUpdated   = 0,
           @RowsUnchanged = 0;
END;
GO

GRANT EXECUTE ON dbo.Custom_ImportPurchaseOrder TO Custom_NiFiImport;
GO
