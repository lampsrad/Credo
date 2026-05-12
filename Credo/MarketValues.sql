-- Run once against the Credo database to create the MarketValues table.
IF OBJECT_ID('dbo.MarketValues', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MarketValues
    (
        ID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MarketValues PRIMARY KEY,
        Account NVARCHAR(450) NULL,
        [Date]  DATE NOT NULL,
        [Value] DECIMAL(18,4) NOT NULL
    );

    CREATE INDEX IX_MarketValues_Account_Date ON dbo.MarketValues (Account, [Date]);
END;
