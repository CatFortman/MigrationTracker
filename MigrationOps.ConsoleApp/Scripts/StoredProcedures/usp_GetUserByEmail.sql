-- Tags: db1
CREATE OR ALTER PROCEDURE dbo.usp_GetUserByEmail
    @Email NVARCHAR(255)
AS
BEGIN
    SELECT UserId, UserName, Email, CreatedOn
    FROM dbo.Users
    WHERE Email = @Email;
END
