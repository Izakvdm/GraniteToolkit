/* ============================================================================
   GraniteWMS NiFi Import Framework
   99_Remove_Old_Design.sql  (optional, only where the first version was used)

   Removes objects from the earlier per-feed design that the framework no
   longer uses. Run AFTER 01 to 05 are deployed and the new flow is running.
   The old per-feed log tables hold import history: copy them out first if
   anyone still needs it.
   ========================================================================= */

IF OBJECT_ID(N'dbo.[Custom_ImportMasterItems ]', N'P') IS NOT NULL
    DROP PROCEDURE dbo.[Custom_ImportMasterItems ];
IF OBJECT_ID(N'dbo.Custom_ImportMasterItems', N'P') IS NOT NULL
    DROP PROCEDURE dbo.Custom_ImportMasterItems;

IF OBJECT_ID(N'dbo.Custom_MasterItemImportLog',     N'U') IS NOT NULL DROP TABLE dbo.Custom_MasterItemImportLog;
IF OBJECT_ID(N'dbo.Custom_TradingPartnerImportLog', N'U') IS NOT NULL DROP TABLE dbo.Custom_TradingPartnerImportLog;
IF OBJECT_ID(N'dbo.Custom_SalesOrderImportLog',     N'U') IS NOT NULL DROP TABLE dbo.Custom_SalesOrderImportLog;
IF OBJECT_ID(N'dbo.Custom_PurchaseOrderImportLog',  N'U') IS NOT NULL DROP TABLE dbo.Custom_PurchaseOrderImportLog;
GO
