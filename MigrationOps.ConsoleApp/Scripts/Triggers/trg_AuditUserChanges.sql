-- Tags: db1
CREATE OR ALTER TRIGGER dbo.trg_AuditUserChanges
ON dbo.Users
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.AuditLogs (Action, PerformedBy, PerformedOn)
    SELECT 'Users table changed', SYSTEM_USER, GETDATE();
END
