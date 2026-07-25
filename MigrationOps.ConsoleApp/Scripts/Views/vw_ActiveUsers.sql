-- Tags: db1
CREATE OR ALTER VIEW dbo.vw_ActiveUsers
AS
SELECT UserId, UserName, Email, CreatedOn
FROM dbo.Users;
