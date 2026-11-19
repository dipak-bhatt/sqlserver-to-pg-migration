-- Demo source for sql_to_pg_migration: a small multi-tenant SQL Server schema.
-- Run with:  sqlcmd -S .\SQLEXPRESS -E -i samples\demo\01_source_sqlserver.sql
IF DB_ID(N'demo_src') IS NULL CREATE DATABASE demo_src;
GO
USE demo_src;
GO
DROP TABLE IF EXISTS dbo.EmployeeAddress, dbo.Employee, dbo.Department, dbo.Country;
GO
CREATE TABLE dbo.Country (
    CountryId   int           NOT NULL PRIMARY KEY,
    CountryName nvarchar(80)  NOT NULL,
    IsoCode     char(3)       NOT NULL
);
CREATE TABLE dbo.Department (
    DepartmentID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TenantId     uniqueidentifier  NULL,
    DeptName     nvarchar(100)     NOT NULL,
    IsActive     bit               NOT NULL DEFAULT 1
);
CREATE TABLE dbo.Employee (
    EmployeeID   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TenantId     uniqueidentifier  NULL,
    DepartmentID int               NOT NULL REFERENCES dbo.Department (DepartmentID),
    FullName     nvarchar(200)     NOT NULL,
    Email        varchar(200)      NULL,
    Salary       money             NULL,
    JoinedOn     datetime          NULL,
    UpdatedAt    datetimeoffset    NULL,
    Photo        varbinary(max)    NULL,
    IsDeleted    bit               NOT NULL DEFAULT 0
);
CREATE TABLE dbo.EmployeeAddress (
    EmployeeAddressID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TenantId          uniqueidentifier  NULL,
    EmployeeID        int               NOT NULL REFERENCES dbo.Employee (EmployeeID),
    City              nvarchar(80)      NULL,
    LegacyZone        nvarchar(10)      NULL
);
GO
DECLARE @a uniqueidentifier = '11111111-1111-1111-1111-111111111111';
DECLARE @b uniqueidentifier = '22222222-2222-2222-2222-222222222222';
INSERT dbo.Country VALUES (1, N'Nepal', 'NPL'), (2, N'India', 'IND');
INSERT dbo.Department (TenantId, DeptName) VALUES (@a, N'Finance'), (@a, N'People'), (@b, N'Ops'), (NULL, N'Unclaimed');
INSERT dbo.Employee (TenantId, DepartmentID, FullName, Email, Salary, JoinedOn, UpdatedAt, Photo)
VALUES (@a, 1, N'Asha Rai', 'asha@example.test', 1500.25, '2020-01-15 10:00', SYSDATETIMEOFFSET(), 0x89504E47),
       (@a, 2, N'Bikram Thapa', NULL, NULL, '2021-06-01 09:30', NULL, NULL),
       (@b, 3, N'Chen Li', 'chen@example.test', 2100, '2019-03-03 08:00', NULL, NULL);
INSERT dbo.EmployeeAddress (TenantId, EmployeeID, City, LegacyZone) VALUES (@a, 1, N'Kathmandu', N'Z1'), (@a, 2, NULL, NULL), (@b, 3, N'Pune', N'Z9');
GO
