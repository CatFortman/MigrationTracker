-- Tags: db1
CREATE OR ALTER FUNCTION dbo.fn_GetActiveUserCount()
RETURNS INT
AS
BEGIN
    DECLARE @Count INT;
    SELECT @Count = COUNT(*) FROM dbo.Users;
    RETURN @Count;
END
