USE tempdb

-- Creating table with various data types
IF (OBJECT_ID('dbo.TestTable') IS NOT NULL) DROP TABLE dbo.TestTable;
CREATE TABLE [dbo].[TestTable]
(
    [Id] [uniqueidentifier] NOT NULL DEFAULT NEWID(),
    [ModifiedDate] [datetime2](7) NOT NULL DEFAULT SYSDATETIME(),
    [TinyIntCol] [tinyint] NOT NULL,
    [SmallIntCol] [smallint] NOT NULL,
    [IntCol] [int] NOT NULL,
    [BigIntCol] [bigint] NOT NULL,
    [DecimalCol] [decimal](18,4) NOT NULL,
    [MoneyCol] [money] NOT NULL,
    [FloatCol] [float] NOT NULL,
    [RealCol] [real] NOT NULL,
    [CharCol] [char](10) NOT NULL,
    [VarcharCol] [varchar](50) NOT NULL,
    [NvarcharCol] [nvarchar](100) NOT NULL,
    [TextCol] [text] NOT NULL,
    [NtextCol] [ntext] NOT NULL,
    [BinaryCol] [binary](16) NOT NULL,
    [VarbinaryCol] [varbinary](50) NOT NULL,
    [DateCol] [date] NOT NULL,
    [TimeCol] [time](7) NOT NULL,
    [DateTimeCol] [datetime] NOT NULL,
    [DateTimeOffsetCol] [datetimeoffset](7) NOT NULL,
    [BitCol] [bit] NOT NULL,
    [GeographyCol] [geography] NOT NULL,
    [HierarchyIdCol] [hierarchyid] NOT NULL,
    [SqlVariantCol] [sql_variant] NOT NULL,
    [XmlCol] [xml] NOT NULL
) 
GO

-- Adding primary key
ALTER TABLE [dbo].[TestTable] ADD CONSTRAINT [PK_TestTable] PRIMARY KEY CLUSTERED ([Id])
GO

-- Creating trigger to update ModifiedDate
CREATE TRIGGER [dbo].[trg_TestTable_UpdateModifiedDate]
ON [dbo].[TestTable]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE t
    SET ModifiedDate = GETDATE()
    FROM [dbo].[TestTable] t
    INNER JOIN inserted i ON t.Id = i.Id;
END
GO

-- Populating table with random data, cross-joining with nums for 1M+ rows
INSERT INTO [dbo].[TestTable] (
    Id,
    ModifiedDate,
    TinyIntCol,
    SmallIntCol,
    IntCol,
    BigIntCol,
    DecimalCol,
    MoneyCol,
    FloatCol,
    RealCol,
    CharCol,
    VarcharCol,
    NvarcharCol,
    TextCol,
    NtextCol,
    BinaryCol,
    VarbinaryCol,
    DateCol,
    TimeCol,
    DateTimeCol,
    DateTimeOffsetCol,
    BitCol,
    GeographyCol,
    HierarchyIdCol,
    SqlVariantCol,
    XmlCol
)
SELECT
    NEWID() AS Id,
    SYSDATETIME() AS ModifiedDate,
    -- Numeric types
    ABS(CHECKSUM(NEWID()) % 256) AS TinyIntCol, -- 0 to 255
    ABS(CHECKSUM(NEWID()) % 32768) AS SmallIntCol, -- -32768 to 32767
    ABS(CHECKSUM(NEWID())) AS IntCol, -- -2^31 to 2^31-1
    CAST(ABS(CHECKSUM(NEWID())) AS bigint) * 1000 AS BigIntCol,
    CAST(ABS(CHECKSUM(NEWID())) % 1000000 / 100.0 AS decimal(18,4)) AS DecimalCol,
    CAST(ABS(CHECKSUM(NEWID())) % 100000 / 100.0 AS money) AS MoneyCol,
    RAND(CHECKSUM(NEWID())) * 1000 AS FloatCol,
    CAST(RAND(CHECKSUM(NEWID())) * 100 AS real) AS RealCol,
    -- String types
    LEFT(CAST(NEWID() AS varchar(36)), 10) AS CharCol, -- Fixed 10 chars
    CAST(NEWID() AS varchar(50)) AS VarcharCol,
    N'Name_' + CAST(n.n AS nvarchar(50)) AS NvarcharCol,
    REPLICATE(CAST(NEWID() AS varchar(36)), 10) AS TextCol,
    REPLICATE(N'Text_' + CAST(n.n AS nvarchar(50)), 5) AS NtextCol,
    -- Binary types
    CAST(NEWID() AS binary(16)) AS BinaryCol,
    CAST(NEWID() AS varbinary(50)) AS VarbinaryCol,
    -- Date/time types
    DATEADD(day, ABS(CHECKSUM(NEWID()) % 3650), '2000-01-01') AS DateCol, -- ~10 years range
    DATEADD(millisecond, ABS(CHECKSUM(NEWID()) % 86400000), '00:00:00') AS TimeCol, -- 24-hour range
    DATEADD(minute, ABS(CHECKSUM(NEWID()) % 5256000), '2000-01-01') AS DateTimeCol, -- ~10 years in minutes
    DATEADD(minute, ABS(CHECKSUM(NEWID()) % 5256000), '2000-01-01') AT TIME ZONE 'UTC' AS DateTimeOffsetCol,
    -- Bit
    ABS(CHECKSUM(NEWID()) % 2) AS BitCol,
    -- Geography (random point in valid range)
    geography::Point(RAND(CHECKSUM(NEWID())) * 90 * (CASE WHEN ABS(CHECKSUM(NEWID()) % 2) = 0 THEN 1 ELSE -1 END),
                    RAND(CHECKSUM(NEWID())) * 180 * (CASE WHEN ABS(CHECKSUM(NEWID()) % 2) = 0 THEN 1 ELSE -1 END), 4326) AS GeographyCol,
    -- HierarchyId (simple path based on n)
    hierarchyid::GetRoot().GetDescendant(
        hierarchyid::Parse('/' + CAST(n.n / 1000 AS varchar(10)) + '/'),
        hierarchyid::Parse('/' + CAST((n.n / 1000 + 1) AS varchar(10)) + '/')
    ) AS HierarchyIdCol,
    -- Sql_variant (cycle through types)
    CASE ABS(CHECKSUM(NEWID()) % 3)
        WHEN 0 THEN CAST(n.n AS sql_variant)
        WHEN 1 THEN CAST(CAST(NEWID() AS varchar(36)) AS sql_variant)
        ELSE CAST(SYSDATETIME() AS sql_variant)
    END AS SqlVariantCol,
    -- Xml
    CAST('<row id="' + CAST(n.n AS varchar(20)) + '">' + CAST(NEWID() AS varchar(36)) + '</row>' AS xml) AS XmlCol
FROM (SELECT TOP (2) object_id AS n FROM sys.objects WHERE OBJECT_ID > 0) n


--FROM Other.[dbo].[nums] n
--WHERE n.n <= 1000000; -- Match your 1M+ rows, adjust if needed
--GO

CREATE TABLE SimpleTestTable (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    ModifiedDate datetime2(7) NOT NULL DEFAULT SYSDATETIME(),
    IntCol int NOT NULL,
    VarcharCol varchar(50) NOT NULL
);
INSERT INTO SimpleTestTable (IntCol, VarcharCol)
SELECT TOP 300000 n, CAST(NEWID() AS varchar(50))
FROM Other.dbo.nums;


SELECT Id,
       ModifiedDate,
       TinyIntCol,
       SmallIntCol,
       IntCol,
       BigIntCol,
       DecimalCol,
       MoneyCol,
       FloatCol,
       RealCol,
       CharCol,
       VarcharCol,
       NvarcharCol,
       TextCol,
       NtextCol,
       BinaryCol,
       VarbinaryCol,
       DateCol,
       TimeCol,
       DateTimeCol,
       DateTimeOffsetCol,
       BitCol,
       GeographyCol,
       HierarchyIdCol,
       SqlVariantCol,
       XmlCol 
FROM dbo.TestTable