/* ============================================================================
   GraniteWMS NiFi Import Framework
   09_NiFi_SqlLogin.sql  (optional, run once per client)

   Creates a dedicated SQL login for NiFi and adds it to the Custom_NiFiImport
   role, which only has:
     - EXECUTE on the Custom_NiFi_* and Custom_Import* procs
     - SELECT, INSERT on the Custom_*Staging tables
     - SELECT on Custom_NiFiImportLog
   The procs run as owner, so NiFi never needs rights on Granite tables.

   Run against the client's Granite database AFTER 01_Framework.sql.
   Set the two values below first. Do not save the password in this file.
   ========================================================================= */

DECLARE @Login    sysname       = N'svc_granite_nifi',
        @Password nvarchar(128) = N'<<set a strong password>>';

IF @Password LIKE N'<<%'
    THROW 50000, 'Set @Password before running this script.', 1;

DECLARE @Sql nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @Login)
BEGIN
    SET @Sql = N'CREATE LOGIN ' + QUOTENAME(@Login)
             + N' WITH PASSWORD = ' + QUOTENAME(@Password, N'''')
             + N', CHECK_POLICY = ON, DEFAULT_DATABASE = ' + QUOTENAME(DB_NAME()) + N';';
    EXEC sys.sp_executesql @Sql;
END;

IF DATABASE_PRINCIPAL_ID(@Login) IS NULL
BEGIN
    SET @Sql = N'CREATE USER ' + QUOTENAME(@Login) + N' FOR LOGIN ' + QUOTENAME(@Login) + N';';
    EXEC sys.sp_executesql @Sql;
END;

IF ISNULL(IS_ROLEMEMBER(N'Custom_NiFiImport', @Login), 0) = 0
BEGIN
    SET @Sql = N'ALTER ROLE Custom_NiFiImport ADD MEMBER ' + QUOTENAME(@Login) + N';';
    EXEC sys.sp_executesql @Sql;
END;

SELECT @Login AS NiFiLogin, DB_NAME() AS GraniteDatabase,
       IS_ROLEMEMBER(N'Custom_NiFiImport', @Login) AS InRole;
GO
