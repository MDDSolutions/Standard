/*
    ====================================================================================================
    TimeSeqId - 38+10 Bit Version
    ====================================================================================================

    Purpose
    -------
    TimeSeqId is a compact, sortable, time-aware database identifier.

    It is stored as binary(6), which is 48 bits total.

    Internal layout:

        [ 38 bits second bucket ][ 10 bits sequence-within-second ]

    The second bucket is the number of seconds since:

        1289-06-18 17:50:56

    This unusual epoch is intentional. It aligns the maximum 38-bit second bucket,
    2^38 - 1, with SQL Server datetime2's maximum second-level value:

        9999-12-31 23:59:59

    The sequence is:

        0 through 1023

    This provides:

        - 6-byte key
        - second-level timestamp precision
        - 1,024 unique values per second per table with the recommended trigger pattern
        - native binary sort by creation time, then sequence
        - extractable approximate creation time
        - compact clustered key
        - human-readable formatted representation

    Display format
    --------------
        yyyy-MM-dd HH:mm:ss#nnnn

    Example
    -------
        2026-05-12 16:59:43#0012

    The # separator is intentionally used instead of "." so the sequence does not look like milliseconds.

    Important behavior
    ------------------
    TimeSeqId has second precision. Fractional seconds are discarded.

    TimeSeqId stores local wall-clock time, not UTC and not an absolute instant.

    This is intentional. The public surface accepts and returns times that users understand:

        dbo.TimeSeqId_Parse
        dbo.TimeSeqId_Make
        dbo.TimeSeqId_GetCreatedAt
        dbo.TimeSeqId_Format

    Because the encoded value is local wall-clock time, daylight saving time transitions can affect
    real-time ordering:

        - During a spring-forward transition, some local wall-clock times do not exist.
        - During a fall-back transition, some local wall-clock times occur twice.
        - During the repeated fall-back hour, an ID generated later in real elapsed time may sort
          before an ID generated earlier in real elapsed time.

    This does not normally break uniqueness. The recommended table trigger below uses the target table's
    existing IDs for the current local second to choose the next sequence. The practical uniqueness limit
    remains 1,024 IDs per table for a given local second. During a repeated fall-back second, both
    appearances of the same local second share that same sequence capacity.

    If a system requires strict absolute chronological order across daylight saving time transitions,
    time zone changes, servers in multiple local time zones, or historical time-zone rule changes, store
    a separate UTC datetime2/datetimeoffset value or use an identifier that encodes an absolute instant.

    Parsing accepts partial date/time values and assumes the minimum unspecified value:

        dbo.TimeSeqId_Parse('2026-05-12')
            -> 2026-05-12 00:00:00#0000

        dbo.TimeSeqId_Parse('2026-05-12 16')
            -> 2026-05-12 16:00:00#0000

        dbo.TimeSeqId_Parse('2026-05-12 16:59')
            -> 2026-05-12 16:59:00#0000

        dbo.TimeSeqId_Parse('2026-05-12 16:59:43')
            -> 2026-05-12 16:59:43#0000

        dbo.TimeSeqId_Parse('2026-05-12 16:59:43#12')
            -> 2026-05-12 16:59:43#0012

    Sort behavior
    -------------
    The 48-bit packed integer is stored in big-endian byte order.

    Therefore SQL Server's native binary sort order matches:

        ORDER BY CreatedAt, Sequence

    This means a clustered primary key on dbo.TimeSeqId sorts chronologically.

    Capacity
    --------
    38 bits of seconds gives:

        274,877,906,944 seconds

    The epoch is chosen so the maximum 38-bit second bucket lands exactly on:

    9999-12-31 23:59:59

    10 bits of sequence gives:

        1,024 IDs per second per table when using the recommended trigger pattern

    ORM notes
    ---------
    dbo.TimeSeqId is an alias type over binary(6). Some ORMs preserve the alias type metadata, while others
    flatten it to binary(6). The database metadata does distinguish it from ordinary binary(6).

    Recommended table pattern
    -------------------------
        CREATE TABLE dbo.ExampleThing
        (
            Id dbo.TimeSeqId NOT NULL
                CONSTRAINT PK_ExampleThing PRIMARY KEY CLUSTERED,

            Name nvarchar(100) NOT NULL,

            CreatedAt AS dbo.TimeSeqId_GetCreatedAt(Id),
            SequenceInSecond AS dbo.TimeSeqId_GetSequence(Id),
            IdText AS dbo.TimeSeqId_Format(Id)
        );

    Recommended generation pattern
    ------------------------------
    Use a table-specific INSTEAD OF INSERT trigger or insert procedure. The recommended trigger pattern:

        - allows multi-row inserts when enough sequence values remain in the current second
        - truncates SYSDATETIME() to the current local second
        - seeks the target table's clustered TimeSeqId key for that second's range
        - uses TOP (1) ... ORDER BY Id DESC to find the latest existing sequence
        - uses UPDLOCK, HOLDLOCK so concurrent inserts for the same second serialize
        - inserts dbo.TimeSeqId_Make(CurrentSecond, LatestSequence + 1 + RowOffset)

    This avoids a separate counter table and avoids cleanup of expired second buckets.
    During a fall-back repeated second, the trigger sees any rows already inserted for that local second
    and continues the sequence from the highest existing ID.

    Recommended insert pattern
    --------------------------
        INSERT dbo.ExampleThing
        (
            Name
        )
        VALUES
        (
            N'Some row'
        );

    ====================================================================================================
*/


/*
    ----------------------------------------------------------------------------------------------------
    1. Create alias type dbo.TimeSeqId
    ----------------------------------------------------------------------------------------------------
*/

IF TYPE_ID(N'dbo.TimeSeqId') IS NULL
BEGIN
    EXEC(N'CREATE TYPE dbo.TimeSeqId FROM binary(6) NOT NULL;');
END;
GO

DECLARE @ExistingBaseTypeName sysname;
DECLARE @ExistingMaxLength smallint;

SELECT
    @ExistingBaseTypeName = base_type.name,
    @ExistingMaxLength = alias_type.max_length
FROM sys.types AS alias_type
INNER JOIN sys.types AS base_type
    ON alias_type.system_type_id = base_type.system_type_id
    AND base_type.user_type_id = base_type.system_type_id
WHERE
    alias_type.is_user_defined = 1
    AND alias_type.name = N'TimeSeqId'
    AND SCHEMA_NAME(alias_type.schema_id) = N'dbo';

IF @ExistingBaseTypeName <> N'binary'
   OR @ExistingMaxLength <> 6
BEGIN
    THROW 51010, 'dbo.TimeSeqId already exists but is not defined as binary(6).', 1;
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    2. dbo.TimeSeqId_Make
    ----------------------------------------------------------------------------------------------------

    Converts a datetime and sequence into binary(6).

    Input:
        @CreatedAt datetime2(7)
        @Sequence int, 0 through 1023

    Output:
        binary(6)

    Notes:
        - Fractional seconds are discarded.
        - Values before epoch return NULL.
        - Byte order is big-endian so binary sort order is chronological.
*/

CREATE OR ALTER FUNCTION dbo.TimeSeqId_Make
(
    @CreatedAt datetime2(7),
    @Sequence int
)
RETURNS binary(6)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @Epoch datetime2(0) = DATETIME2FROMPARTS(1289, 6, 18, 17, 50, 56, 0, 0);
    --intentional epoch value aligns maximum value with datetime2's max value of 9999-12-31 23:59:59
    DECLARE @CreatedSecond datetime2(0);
    DECLARE @SecondBucket bigint;
    DECLARE @PackedValue bigint;

    DECLARE @Byte1 tinyint;
    DECLARE @Byte2 tinyint;
    DECLARE @Byte3 tinyint;
    DECLARE @Byte4 tinyint;
    DECLARE @Byte5 tinyint;
    DECLARE @Byte6 tinyint;

    IF @CreatedAt IS NULL
        RETURN NULL;

    IF @Sequence IS NULL OR @Sequence < 0 OR @Sequence > 1023
        RETURN NULL;

    /*
        Truncate to the second.
    */
    SET @CreatedSecond =
        DATETIME2FROMPARTS
        (
            DATEPART(year, @CreatedAt),
            DATEPART(month, @CreatedAt),
            DATEPART(day, @CreatedAt),
            DATEPART(hour, @CreatedAt),
            DATEPART(minute, @CreatedAt),
            DATEPART(second, @CreatedAt),
            0,
            0
        );

    SET @SecondBucket = DATEDIFF_BIG(second, @Epoch, @CreatedSecond);

    IF @SecondBucket < 0 OR @SecondBucket > 274877906943
        RETURN NULL;

    /*
        38+10 packing:

            PackedValue = SecondBucket * 1024 + Sequence

        Multiplying by 1024 is equivalent to shifting left by 10 bits.
    */
    SET @PackedValue = (@SecondBucket * 1024) + @Sequence;

    /*
        Manual big-endian packing into 6 bytes.

        6 bytes = 48 bits.
        Max packed value = 2^48 - 1.
    */
    SET @Byte1 = CONVERT(tinyint, (@PackedValue / 1099511627776) % 256); -- 256^5
    SET @Byte2 = CONVERT(tinyint, (@PackedValue / 4294967296) % 256);    -- 256^4
    SET @Byte3 = CONVERT(tinyint, (@PackedValue / 16777216) % 256);      -- 256^3
    SET @Byte4 = CONVERT(tinyint, (@PackedValue / 65536) % 256);         -- 256^2
    SET @Byte5 = CONVERT(tinyint, (@PackedValue / 256) % 256);           -- 256^1
    SET @Byte6 = CONVERT(tinyint, @PackedValue % 256);                   -- 256^0

    RETURN
        CAST(@Byte1 AS binary(1)) +
        CAST(@Byte2 AS binary(1)) +
        CAST(@Byte3 AS binary(1)) +
        CAST(@Byte4 AS binary(1)) +
        CAST(@Byte5 AS binary(1)) +
        CAST(@Byte6 AS binary(1));
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    3. dbo.TimeSeqId_GetPackedValue
    ----------------------------------------------------------------------------------------------------

    Internal helper.

    Converts binary(6) to the logical 48-bit packed integer.

    PackedValue = SecondBucket * 1024 + Sequence
*/

CREATE OR ALTER FUNCTION dbo.TimeSeqId_GetPackedValue
(
    @Id binary(6)
)
RETURNS bigint
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @Byte1 int;
    DECLARE @Byte2 int;
    DECLARE @Byte3 int;
    DECLARE @Byte4 int;
    DECLARE @Byte5 int;
    DECLARE @Byte6 int;

    IF @Id IS NULL OR DATALENGTH(@Id) <> 6
        RETURN NULL;

    SET @Byte1 = CONVERT(tinyint, SUBSTRING(@Id, 1, 1));
    SET @Byte2 = CONVERT(tinyint, SUBSTRING(@Id, 2, 1));
    SET @Byte3 = CONVERT(tinyint, SUBSTRING(@Id, 3, 1));
    SET @Byte4 = CONVERT(tinyint, SUBSTRING(@Id, 4, 1));
    SET @Byte5 = CONVERT(tinyint, SUBSTRING(@Id, 5, 1));
    SET @Byte6 = CONVERT(tinyint, SUBSTRING(@Id, 6, 1));

    RETURN
        CONVERT(bigint, @Byte1) * 1099511627776 +
        CONVERT(bigint, @Byte2) * 4294967296 +
        CONVERT(bigint, @Byte3) * 16777216 +
        CONVERT(bigint, @Byte4) * 65536 +
        CONVERT(bigint, @Byte5) * 256 +
        CONVERT(bigint, @Byte6);
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    4. dbo.TimeSeqId_GetCreatedAt
    ----------------------------------------------------------------------------------------------------

    Extracts the second-level datetime from a TimeSeqId.

    Input:
        @Id binary(6)

    Output:
        datetime2(0)
*/

CREATE OR ALTER FUNCTION dbo.TimeSeqId_GetCreatedAt
(
    @Id binary(6)
)
RETURNS datetime2(0)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @Epoch datetime2(0) = DATETIME2FROMPARTS(1289, 6, 18, 17, 50, 56, 0, 0);
    --intentional epoch value aligns maximum value with datetime2's max value of 9999-12-31 23:59:59

    DECLARE @PackedValue bigint;
    DECLARE @SecondBucket bigint;

    DECLARE @Days int;
    DECLARE @RemainingSeconds int;

    IF @Id IS NULL OR DATALENGTH(@Id) <> 6
        RETURN NULL;

    SET @PackedValue = dbo.TimeSeqId_GetPackedValue(@Id);
    SET @SecondBucket = @PackedValue / 1024;

    /*
        DATEADD in many SQL Server versions expects int increments, so split the second count into
        days and remaining seconds.
    */
    SET @Days = CONVERT(int, @SecondBucket / 86400);
    SET @RemainingSeconds = CONVERT(int, @SecondBucket % 86400);

    RETURN DATEADD(second, @RemainingSeconds, DATEADD(day, @Days, @Epoch));
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    5. dbo.TimeSeqId_GetSequence
    ----------------------------------------------------------------------------------------------------

    Extracts the sequence-within-second from a TimeSeqId.

    Input:
        @Id binary(6)

    Output:
        int, 0 through 1023
*/

CREATE OR ALTER FUNCTION dbo.TimeSeqId_GetSequence
(
    @Id binary(6)
)
RETURNS int
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @PackedValue bigint;

    IF @Id IS NULL OR DATALENGTH(@Id) <> 6
        RETURN NULL;

    SET @PackedValue = dbo.TimeSeqId_GetPackedValue(@Id);

    RETURN CONVERT(int, @PackedValue % 1024);
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    6. dbo.TimeSeqId_Format
    ----------------------------------------------------------------------------------------------------

    Formats a TimeSeqId as:

        yyyy-MM-dd HH:mm:ss#nnnn

    Example:
        2026-05-12 16:59:43#0012

    The sequence is left-padded to four digits because the max value is 1023.
*/

CREATE OR ALTER FUNCTION dbo.TimeSeqId_Format
(
    @Id binary(6)
)
RETURNS varchar(32)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @CreatedAt datetime2(0);
    DECLARE @Sequence int;

    IF @Id IS NULL OR DATALENGTH(@Id) <> 6
        RETURN NULL;

    SET @CreatedAt = dbo.TimeSeqId_GetCreatedAt(@Id);
    SET @Sequence = dbo.TimeSeqId_GetSequence(@Id);

    RETURN
        CONVERT(char(19), @CreatedAt, 120) +
        '#' +
        RIGHT('0000' + CONVERT(varchar(4), @Sequence), 4);
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    7. dbo.TimeSeqId_Parse
    ----------------------------------------------------------------------------------------------------

    Parses text into a TimeSeqId.

    Accepted examples:

        2026-05-12
        2026-05-12 16
        2026-05-12 16:59
        2026-05-12 16:59:43
        2026-05-12 16:59:43.123
        2026-05-12#12
        2026-05-12 16#12
        2026-05-12 16:59#12
        2026-05-12 16:59:43#12
        2026-05-12 16:59:43#0012

    Missing time parts use the minimum value:

        date only       -> midnight
        date + hour     -> that hour, minute zero, second zero
        date + minute   -> that minute, second zero
        no sequence     -> sequence zero

    Fractional seconds are accepted but discarded.

    Invalid input returns NULL.
*/

CREATE OR ALTER FUNCTION dbo.TimeSeqId_Parse
(
    @Text varchar(64)
)
RETURNS binary(6)
AS
BEGIN
    DECLARE @CleanText varchar(64);
    DECLARE @HashPosition int;

    DECLARE @DateTimeText varchar(40);
    DECLARE @SequenceText varchar(20);

    DECLARE @CreatedAt datetime2(7);
    DECLARE @Sequence int = 0;

    IF @Text IS NULL
        RETURN NULL;

    SET @CleanText = LTRIM(RTRIM(REPLACE(@Text, 'T', ' ')));

    IF @CleanText = ''
        RETURN NULL;

    SET @HashPosition = CHARINDEX('#', @CleanText);

    IF @HashPosition > 0
    BEGIN
        SET @DateTimeText = LTRIM(RTRIM(LEFT(@CleanText, @HashPosition - 1)));
        SET @SequenceText = LTRIM(RTRIM(SUBSTRING(@CleanText, @HashPosition + 1, 20)));

        IF @DateTimeText = '' OR @SequenceText = ''
            RETURN NULL;

        SET @Sequence = TRY_CONVERT(int, @SequenceText);

        IF @Sequence IS NULL OR @Sequence < 0 OR @Sequence > 1023
            RETURN NULL;
    END
    ELSE
    BEGIN
        SET @DateTimeText = @CleanText;
    END;

    /*
        First try normal conversion.

        This handles:
            yyyy-MM-dd
            yyyy-MM-dd HH:mm
            yyyy-MM-dd HH:mm:ss
            yyyy-MM-dd HH:mm:ss.fffffff
    */
    SET @CreatedAt = TRY_CONVERT(datetime2(7), @DateTimeText, 120);

    /*
        SQL Server does not reliably accept yyyy-MM-dd HH as a datetime.
        Treat it as yyyy-MM-dd HH:00:00.
    */
    IF @CreatedAt IS NULL
    BEGIN
        IF LEN(@DateTimeText) = 13
           AND SUBSTRING(@DateTimeText, 5, 1) = '-'
           AND SUBSTRING(@DateTimeText, 8, 1) = '-'
           AND SUBSTRING(@DateTimeText, 11, 1) = ' '
        BEGIN
            SET @CreatedAt = TRY_CONVERT(datetime2(7), @DateTimeText + ':00:00', 120);
        END;
    END;

    IF @CreatedAt IS NULL
        RETURN NULL;

    RETURN dbo.TimeSeqId_Make(@CreatedAt, @Sequence);
END;
GO


/*
    ----------------------------------------------------------------------------------------------------
    8. Recommended table and trigger pattern
    ----------------------------------------------------------------------------------------------------

    Uncomment this section in a scratch database if you want a quick test table and trigger.

    In real applications, use this pattern in your own tables. The trigger treats Id like an identity:
    callers should not provide it. The trigger allows multi-row automatic ID assignment when enough
    sequence values remain in the current local second. Multi-row sequence assignment order is not
    guaranteed. If sequence order matters, set @MaxAutomaticIdsPerInsert to 1 or use a loader/procedure
    with an explicit ordinal. Bulk/backfill processes that need explicit IDs should use a purpose-built
    loader or temporarily replace/disable the trigger.
*/

/*
IF OBJECT_ID(N'dbo.ExampleThing', N'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.ExampleThing;
END;
GO

CREATE TABLE dbo.ExampleThing
(
    Id dbo.TimeSeqId NOT NULL
        CONSTRAINT PK_ExampleThing PRIMARY KEY CLUSTERED,

    Name nvarchar(100) NOT NULL,

    CreatedAt AS dbo.TimeSeqId_GetCreatedAt(Id),
    SequenceInSecond AS dbo.TimeSeqId_GetSequence(Id),
    IdText AS dbo.TimeSeqId_Format(Id)
);
GO

CREATE OR ALTER TRIGGER dbo.TR_ExampleThing_TimeSeqId
ON dbo.ExampleThing
INSTEAD OF INSERT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @InsertedRowCount int;
    DECLARE @MaxAutomaticIdsPerInsert int = 1024; -- Change this to 1 to allow only single-row inserts.
    DECLARE @CreatedAt datetime2(0);
    DECLARE @FirstSequence int;

    SELECT
        @InsertedRowCount = COUNT(*)
    FROM inserted;

    IF EXISTS
    (
        SELECT 1
        FROM inserted
        WHERE Id IS NOT NULL
    )
    BEGIN
        THROW 51032, 'dbo.ExampleThing.Id is generated automatically and should not be specified.', 1;
    END;

    IF @InsertedRowCount > @MaxAutomaticIdsPerInsert
    BEGIN
        THROW 51030, 'dbo.ExampleThing automatic TimeSeqId assignment received too many rows.', 1;
    END;

    IF @InsertedRowCount > 0
    BEGIN
        DECLARE @Now datetime2(7) = SYSDATETIME();
        DECLARE @Low dbo.TimeSeqId;
        DECLARE @High dbo.TimeSeqId;
        DECLARE @LastId dbo.TimeSeqId;

        SET @CreatedAt =
            DATETIME2FROMPARTS
            (
                DATEPART(year, @Now),
                DATEPART(month, @Now),
                DATEPART(day, @Now),
                DATEPART(hour, @Now),
                DATEPART(minute, @Now),
                DATEPART(second, @Now),
                0,
                0
            );

        SET @Low = dbo.TimeSeqId_Make(@CreatedAt, 0);
        SET @High = dbo.TimeSeqId_Make(@CreatedAt, 1023);

        SELECT TOP (1)
            @LastId = Id
        FROM dbo.ExampleThing WITH (UPDLOCK, HOLDLOCK)
        WHERE
            Id >= @Low
            AND Id <= @High
        ORDER BY Id DESC;

        SET @FirstSequence = ISNULL(dbo.TimeSeqId_GetSequence(@LastId), -1) + 1;

        IF @FirstSequence + @InsertedRowCount - 1 > 1023
        BEGIN
            THROW 51031, 'dbo.ExampleThing TimeSeqId sequence exhausted for the current local second.', 1;
        END;
    END;

    ;WITH RowsToInsert AS
    (
        SELECT
            Id,
            Name,
            /*
                SQL Server does not guarantee the caller's multi-row insert order here. These row
                offsets are only for assigning distinct IDs within the current second. If sequence order
                matters, set @MaxAutomaticIdsPerInsert to 1 or use a loader/procedure with an explicit
                ordinal column.
            */
            CONVERT
            (
                int,
                ROW_NUMBER() OVER (ORDER BY (SELECT 0)) - 1
            ) AS AutomaticRowIndex
        FROM inserted
    )
    INSERT dbo.ExampleThing
    (
        Id,
        Name
    )
    SELECT
        dbo.TimeSeqId_Make(@CreatedAt, @FirstSequence + AutomaticRowIndex),
        Name
    FROM RowsToInsert;
END;
*/


/*
    ----------------------------------------------------------------------------------------------------
    9. Optional parse test
    ----------------------------------------------------------------------------------------------------
*/

/*
SELECT
    dbo.TimeSeqId_Format(dbo.TimeSeqId_Parse('2026-05-12')) AS A,
    dbo.TimeSeqId_Format(dbo.TimeSeqId_Parse('2026-05-12 16')) AS B,
    dbo.TimeSeqId_Format(dbo.TimeSeqId_Parse('2026-05-12 16:59')) AS C,
    dbo.TimeSeqId_Format(dbo.TimeSeqId_Parse('2026-05-12 16:59:43')) AS D,
    dbo.TimeSeqId_Format(dbo.TimeSeqId_Parse('2026-05-12 16:59:43#12')) AS E,
    dbo.TimeSeqId_Format(dbo.TimeSeqId_Parse('2026-05-12 16:59:43#1023')) AS F;
GO
*/


/*
    Expected shape:

        A  2026-05-12 00:00:00#0000
        B  2026-05-12 16:00:00#0000
        C  2026-05-12 16:59:00#0000
        D  2026-05-12 16:59:43#0000
        E  2026-05-12 16:59:43#0012
        F  2026-05-12 16:59:43#1023
*/


/*
    ----------------------------------------------------------------------------------------------------
    10. Optional sort test
    ----------------------------------------------------------------------------------------------------

    Uncomment this section after creating dbo.ExampleThing.

    It inserts deliberately scrambled values and verifies that ORDER BY Id matches:

        ORDER BY CreatedAt, SequenceInSecond

    The trigger is disabled during this test because the test uses a multi-row explicit insert.
*/

/*
DELETE FROM dbo.ExampleThing;
DISABLE TRIGGER dbo.TR_ExampleThing_TimeSeqId ON dbo.ExampleThing;

INSERT dbo.ExampleThing
(
    Id,
    Name
)
VALUES
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#12'),   N'2026-05-12 16:59:43 seq 12'),
    (dbo.TimeSeqId_Parse('2026-05-12'),               N'2026-05-12 midnight seq 0'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#2'),    N'2026-05-12 16:59:43 seq 2'),
    (dbo.TimeSeqId_Parse('2126-05-13'),               N'2126-05-13 midnight seq 0'),
    (dbo.TimeSeqId_Parse('2026-05-12 16'),            N'2026-05-12 16:00:00 seq 0'),
    (dbo.TimeSeqId_Parse('2025-12-31 23:59:59#1023'), N'2025-12-31 23:59:59 seq 1023'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:42#1023'), N'2026-05-12 16:59:42 seq 1023'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#0'),    N'2026-05-12 16:59:43 seq 0'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#1023'), N'2026-05-12 16:59:43 seq 1023'),
    (dbo.TimeSeqId_Parse('1289-06-18 17:50:56'), N'aligned epoch start'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:42#0'),    N'2026-05-12 16:59:42 seq 0'),
    (dbo.TimeSeqId_Parse('2026-05-12 00:00:01#0'),    N'2026-05-12 00:00:01 seq 0'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#1'),    N'2026-05-12 16:59:43 seq 1'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#10'),   N'2026-05-12 16:59:43 seq 10'),
    (dbo.TimeSeqId_Parse('2026-05-12 16:59:43#100'),  N'2026-05-12 16:59:43 seq 100'),
    (dbo.TimeSeqId_Parse('9999-12-31 23:59:59#1023'), N'max supported TimeSeqId');

SELECT
    Id,
    IdText,
    CreatedAt,
    SequenceInSecond,
    Name
FROM dbo.ExampleThing
ORDER BY Id;

WITH SortCheck AS
(
    SELECT
        Id,
        ROW_NUMBER() OVER (ORDER BY Id) AS BinarySortOrder,
        ROW_NUMBER() OVER (ORDER BY CreatedAt, SequenceInSecond) AS DecodedSortOrder
    FROM dbo.ExampleThing
)
SELECT
    CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM SortCheck
            WHERE BinarySortOrder <> DecodedSortOrder
        )
        THEN 'FAIL: binary sort does not match decoded sort'
        ELSE 'PASS: binary sort matches decoded sort'
    END AS SortTestResult;

ENABLE TRIGGER dbo.TR_ExampleThing_TimeSeqId ON dbo.ExampleThing;
GO
*/


/*
    ----------------------------------------------------------------------------------------------------
    11. Optional trigger generation test
    ----------------------------------------------------------------------------------------------------

    Uncomment after creating dbo.ExampleThing.
*/

/*
INSERT dbo.ExampleThing
(
    Name
)
VALUES
(
    N'Generated by dbo.TR_ExampleThing_TimeSeqId'
);

SELECT
    Id,
    IdText,
    CreatedAt,
    SequenceInSecond,
    Name
FROM dbo.ExampleThing
ORDER BY Id;
GO
*/