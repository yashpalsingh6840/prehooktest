-- Backing tables for SyntheticScriptComponentSeams.dtsx (ssisx generate --seams).
--
-- The Script Component synthesizes FullName and IsValid, which exist nowhere on its input --
-- so the generated transform can only populate them through a Fill_* seam a human implements.
-- Seed rows deliberately cover the boundaries the ported logic actually turns on: an ordinary
-- row, one with padding around both names, and one with an empty LastName (which must make
-- IsValid false AND must NOT leave a trailing space in FullName).
--
-- Run against .\SQLFORPOC_2022 / SsisPoC.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticScriptSeamsTarget;
DROP TABLE IF EXISTS dbo.SyntheticScriptSeamsInput;
GO

CREATE TABLE dbo.SyntheticScriptSeamsInput
(
    ID        INT           NOT NULL PRIMARY KEY,
    FirstName NVARCHAR(50)  NOT NULL,
    LastName  NVARCHAR(50)  NOT NULL
);
GO

CREATE TABLE dbo.SyntheticScriptSeamsTarget
(
    ID        INT           NOT NULL PRIMARY KEY,
    FirstName NVARCHAR(50)  NOT NULL,
    LastName  NVARCHAR(50)  NOT NULL,
    FullName  NVARCHAR(100) NOT NULL,
    IsValid   BIT           NOT NULL
);
GO

INSERT dbo.SyntheticScriptSeamsInput (ID, FirstName, LastName) VALUES
    (1, N'Alice',    N'Smith'),      -- ordinary            -> 'Alice Smith'      / 1
    (2, N'  Bob  ',  N'  Jones  '),  -- padded both sides   -> 'Bob Jones'        / 1
    (3, N'Carol',    N'   ');        -- empty last name     -> 'Carol' (no trailing space) / 0
GO
