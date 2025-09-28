using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;


namespace DBEngineUnitTests
{
    [TestClass]
    public class DBEngineTests
    {
        string ConnString = "server=MDD-SQL2022;database=tempdb;Trusted_Connection=true;";
        DBEngine _db;
        string testorderupdate = "UPDATE dbo.TestOrders SET CustomerName = @CustomerName, Amount = @Amount WHERE OrderId = @Id AND RowVersion = @RowVersion;SELECT * FROM dbo.TestOrders WHERE OrderId = @Id;";
        [TestInitialize]
        public void Initialize()
        {
            if (_db == null)
            {
                _db = new DBEngine(ConnString, "DBEngineUnitTests");
                _db.AllowAdHoc = true;
                _db.Tracking = ObjectTracking.IfAvailable;
                DBEngine.Default = _db;

                //set up very small table for basic tests
                var stmt = @"
IF OBJECT_ID('dbo.TestOrders', 'U') IS NOT NULL DROP TABLE dbo.TestOrders;
CREATE TABLE dbo.TestOrders (
    OrderId INT PRIMARY KEY IDENTITY,
    CustomerName NVARCHAR(100) NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    RowVersion ROWVERSION
);

INSERT dbo.TestOrders (CustomerName, Amount) VALUES
('Alice', 10.00),
('Bob',   20.00);";
                _db.SqlRunStatement(stmt, -1, null);

                //set up table with all basic data types
                stmt = @"
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
    CAST('<row id=""' + CAST(n.n AS varchar(20)) + '"">' + CAST(NEWID() AS varchar(36)) + '</row>' AS xml) AS XmlCol
FROM (SELECT TOP (2) object_id AS n FROM sys.objects WHERE OBJECT_ID > 0) n
";
                _db.ExecuteScript(stmt);
            }
        }

        [TestMethod]
        public void CanLoadOrdersAndTrackINPC()
        {
            var orders = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT * FROM dbo.TestOrders", false);
            Assert.IsTrue(orders.Count > 0);

            var tracker = _db.GetTracker<TestOrderINPC>();
            Assert.AreEqual(orders.Count, tracker.Count); // crude check

            var tracked = tracker.TryGet(orders[0].Id, out var t) ? t : null;
            Assert.AreEqual(DirtyCheckMode.Cached, tracked.DirtyCheckMode);
        }
        [TestMethod]
        public void CanLoadOrdersAndTrackPOCO()
        {
            var orders = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT * FROM dbo.TestOrders", false);
            Assert.IsTrue(orders.Count > 0);

            var tracker = _db.GetTracker<TestOrderPOCO>();
            Assert.AreEqual(orders.Count, tracker.Count); // crude check

            var tracked = tracker.TryGet(orders[0].Id, out var t) ? t : null;
            Assert.AreEqual(DirtyCheckMode.FullScan, tracked.DirtyCheckMode);
        }
        [TestMethod]
        public void CanLoadOrdersAndTrackNO()
        {
            var orders = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT * FROM dbo.TestOrders", false);
            Assert.IsTrue(orders.Count > 0);

            var tracker = _db.GetTracker<TestOrderNO>();
            Assert.AreEqual(orders.Count, tracker.Count); // crude check

            var tracked = tracker.TryGet(orders[0].Id, out var t) ? t : null;
            Assert.AreEqual(DirtyCheckMode.Advanced, tracked.DirtyCheckMode);
        }

        [TestMethod]
        public void MarksEntityDirtyWhenPropertyChangesINPC()
        {
            var order = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var tracker = _db.GetTracker<TestOrderINPC>();

            var tracked = tracker.TryGet(order.Id, out var t) ? t : null;
            Assert.IsNotNull(tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            order.CustomerName = "Changed!";
            Assert.AreEqual(TrackedState.Modified, tracked.State);
        }
        [TestMethod]
        public void MarksEntityDirtyWhenPropertyChangesNO()
        {
            var order = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var tracker = _db.GetTracker<TestOrderNO>();

            var tracked = tracker.TryGet(order.Id, out var t) ? t : null;
            Assert.IsNotNull(tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            order.CustomerName = "Changed!";
            Assert.AreEqual(TrackedState.Modified, tracked.State);
        }
        [TestMethod]
        public void MarksEntityDirtyWhenPropertyChangesPOCO()
        {
            var order = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var tracker = _db.GetTracker<TestOrderPOCO>();

            var tracked = tracker.TryGet(order.Id, out var t) ? t : null;
            Assert.IsNotNull(tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            order.CustomerName = "Changed!";
            Assert.AreEqual(TrackedState.Modified, tracked.State);
        }

        [TestMethod]
        public void UpdateRoundtripSucceedsINPC()
        {
            var order2 = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var oldName2 = order2.CustomerName;
            order2.CustomerName = oldName2 + "_X";

            bool ok = _db.RunSqlUpdate(order2,
                "UPDATE dbo.TestOrders SET CustomerName=@Name WHERE OrderId=@Id; SELECT * FROM dbo.TestOrders WHERE OrderId=@Id;",
                false, -1, null,
                new SqlParameter("@Id", order2.Id),
                new SqlParameter("@Name", order2.CustomerName));

            Assert.IsTrue(ok);
            Assert.AreEqual(TrackedState.Unchanged, _db.GetTracker<TestOrderINPC>().TryGet(order2.Id, out var tracked2) ? tracked2.State : TrackedState.Invalid);
        }
        [TestMethod]
        public void UpdateRoundtripSucceedsNO()
        {
            var order2 = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var oldName2 = order2.CustomerName;
            order2.CustomerName = oldName2 + "_X";

            bool ok = _db.RunSqlUpdate(order2,
                "UPDATE dbo.TestOrders SET CustomerName=@Name WHERE OrderId=@Id; SELECT * FROM dbo.TestOrders WHERE OrderId=@Id;",
                false, -1, null,
                new SqlParameter("@Id", order2.Id),
                new SqlParameter("@Name", order2.CustomerName));

            Assert.IsTrue(ok);
            Assert.AreEqual(TrackedState.Unchanged, _db.GetTracker<TestOrderNO>().TryGet(order2.Id, out var tracked2) ? tracked2.State : TrackedState.Invalid);
        }
        [TestMethod]
        public void UpdateRoundtripSucceedsPOCO()
        {
            var order2 = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var oldName2 = order2.CustomerName;
            order2.CustomerName = oldName2 + "_X";

            bool ok = _db.RunSqlUpdate(order2,
                "UPDATE dbo.TestOrders SET CustomerName=@Name WHERE OrderId=@Id; SELECT * FROM dbo.TestOrders WHERE OrderId=@Id;",
                false, -1, null,
                new SqlParameter("@Id", order2.Id),
                new SqlParameter("@Name", order2.CustomerName));

            Assert.IsTrue(ok);
            Assert.AreEqual(TrackedState.Unchanged, _db.GetTracker<TestOrderPOCO>().TryGet(order2.Id, out var tracked2) ? tracked2.State : TrackedState.Invalid);
        }

        [TestMethod]
        public async Task GcEntityAndReloadINPC()
        {
            var orders = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false);
            var order = orders.First();
            var key = order.Id;

            var tracker = _db.GetTracker<TestOrderINPC>();
            Assert.AreEqual(TrackedState.Unchanged, tracker.TryGet(key, out var tracked) ? tracked.State : TrackedState.Invalid);

            // Drop strong reference
            orders = null;
            order = null;

            int tries = 0;
            Tracked<TestOrderINPC> tracked2 = null;
            while (tracker.Count > 0 && tries < 10)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100);
                tracker.TryGet(key, out tracked2);
                if (tracked2.State == TrackedState.Invalid)
                    break;
                tries++;
            }

            Assert.AreEqual(TrackedState.Invalid, tracked2.State);

            // Reload same key
            var orders2 = _db.SqlRunQueryWithResults<TestOrderINPC>($"SELECT * FROM dbo.TestOrders WHERE OrderId={key}", false);
            Assert.AreEqual(TrackedState.Unchanged, tracker.TryGet(key, out var tracked3) ? tracked3.State : TrackedState.Invalid);
        }
        [TestMethod]
        public async Task GcEntityAndReloadNO()
        {
            var orders = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT TOP 1 * FROM dbo.TestOrders", false);
            var order = orders.First();
            var key = order.Id;

            var tracker = _db.GetTracker<TestOrderNO>();
            Assert.AreEqual(TrackedState.Unchanged, tracker.TryGet(key, out var tracked) ? tracked.State : TrackedState.Invalid);

            // Drop strong reference
            orders = null;
            order = null;

            int tries = 0;
            Tracked<TestOrderNO> tracked2 = null;
            while (tracker.Count > 0 && tries < 10)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100);
                tracker.TryGet(key, out tracked2);
                if (tracked2.State == TrackedState.Invalid)
                    break;
                tries++;
            }

            Assert.AreEqual(TrackedState.Invalid, tracked2.State);

            // Reload same key
            var orders2 = _db.SqlRunQueryWithResults<TestOrderNO>($"SELECT * FROM dbo.TestOrders WHERE OrderId={key}", false);
            Assert.AreEqual(TrackedState.Unchanged, tracker.TryGet(key, out var tracked3) ? tracked3.State : TrackedState.Invalid);
        }
        [TestMethod]
        public async Task GcEntityAndReloadPOCO()
        {
            var orders = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT TOP 1 * FROM dbo.TestOrders", false);
            var order = orders.First();
            var key = order.Id;

            var tracker = _db.GetTracker<TestOrderPOCO>();
            Assert.AreEqual(TrackedState.Unchanged, tracker.TryGet(key, out var tracked) ? tracked.State : TrackedState.Invalid);

            // Drop strong reference
            orders = null;
            order = null;

            int tries = 0;
            Tracked<TestOrderPOCO> tracked2 = null;
            while (tracker.Count > 0 && tries < 10)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100);
                tracker.TryGet(key, out tracked2);
                if (tracked2.State == TrackedState.Invalid)
                    break;
                tries++;
            }

            Assert.AreEqual(TrackedState.Invalid, tracked2.State);

            // Reload same key
            var orders2 = _db.SqlRunQueryWithResults<TestOrderPOCO>($"SELECT * FROM dbo.TestOrders WHERE OrderId={key}", false);
            Assert.AreEqual(TrackedState.Unchanged, tracker.TryGet(key, out var tracked3) ? tracked3.State : TrackedState.Invalid);
        }

        [TestMethod]
        public void ConcurrencyMismatchThrowsINPC()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();

            var newval = order1.CustomerName + "_Modified!";
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET CustomerName='{newval}' WHERE OrderId={order1.Id}", -1, null);
            order1.CustomerName = order1.CustomerName + "_Changed!";

            object errorkeyval = null;
            try
            {
                _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                    new SqlParameter("@Id", order1.Id));
            }
            catch (DBEngineConcurrencyMismatchException ex)
            {
                errorkeyval = ex.KeyValue;
            }
            Assert.AreEqual(errorkeyval, order1.Id);
        }
        [TestMethod]
        public void ConcurrencyMismatchThrowsPOCO()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();

            var newval = order1.CustomerName + "_Modified!";
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET CustomerName='{newval}' WHERE OrderId={order1.Id}", -1, null);
            order1.CustomerName = order1.CustomerName + "_Changed!";

            object errorkeyval = null;
            try
            {
                _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                    new SqlParameter("@Id", order1.Id));
            }
            catch (DBEngineConcurrencyMismatchException ex)
            {
                errorkeyval = ex.KeyValue;
            }
            Assert.AreEqual(errorkeyval, order1.Id);
        }
        [TestMethod]
        public void ConcurrencyMismatchThrowsNO()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();

            var newval = order1.CustomerName + "_Modified!";
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET CustomerName='{newval}' WHERE OrderId={order1.Id}", -1, null);
            order1.CustomerName = order1.CustomerName + "_Changed!";

            object errorkeyval = null;
            try
            {
                _db.SqlRunQueryWithResults<TestOrderNO>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                    new SqlParameter("@Id", order1.Id));
            }
            catch (DBEngineConcurrencyMismatchException ex)
            {
                errorkeyval = ex.KeyValue;
            }
            Assert.AreEqual(errorkeyval, order1.Id);
        }

        [TestMethod]
        public void ChangedObjectReloadsAndNotifiesINPC()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();

            bool notified = false;
            order1.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TestOrderINPC.CustomerName))
                    notified = true;
            };
            var newval = Guid.NewGuid().ToString();
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET CustomerName='{newval}' WHERE OrderId={order1.Id}", -1, null);

            _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id));

            var tracker = _db.GetTracker<TestOrderINPC>();
            var tracked = tracker.TryGet(order1.Id, out var t) ? t : null;
            Assert.IsNotNull(tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            Assert.AreEqual(newval, order1.CustomerName);
            Assert.IsTrue(notified);
        }
        [TestMethod]
        public void ChangedObjectReloadsAndNotifiesNO()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();

            bool notifiedpu = false;
            NotifierObject.PropertyUpdated += (s, e) =>
            {
                if (s.Equals(order1) && e.PropertyName == nameof(TestOrderNO.CustomerName))
                    notifiedpu = true;
            };



            bool notifiedinpc = false;
            order1.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TestOrderNO.CustomerName))
                    notifiedinpc = true;
            };
            var newval = Guid.NewGuid().ToString();
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET CustomerName='{newval}' WHERE OrderId={order1.Id}", -1, null);

            _db.SqlRunQueryWithResults<TestOrderNO>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id));

            var tracker = _db.GetTracker<TestOrderNO>();
            var tracked = tracker.TryGet(order1.Id, out var t) ? t : null;
            Assert.IsNotNull(tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            Assert.AreEqual(newval, order1.CustomerName);
            Assert.IsTrue(notifiedinpc);
            Assert.IsTrue(notifiedpu);
        }
        [TestMethod]
        public void ChangedObjectReloadsPOCO()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();


            var newval = Guid.NewGuid().ToString();
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET CustomerName='{newval}' WHERE OrderId={order1.Id}", -1, null);

            _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id));

            var tracker = _db.GetTracker<TestOrderPOCO>();
            var tracked = tracker.TryGet(order1.Id, out var t) ? t : null;
            Assert.IsNotNull(tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            Assert.AreEqual(newval, order1.CustomerName);
        }

        [TestMethod]
        public void ChangedObjectEnduresINPC()
        {
            var order1 = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();

            var oldName1 = order1.CustomerName;
            var newName1 = oldName1 + "_X";
            order1.CustomerName = newName1;

            _db.GetTracker<TestOrderINPC>().TryGet(order1.Id, out var tracked1);


            Assert.AreEqual(TrackedState.Modified, tracked1.State);

            var order2 = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id)).First();

            Assert.AreEqual(newName1, order1.CustomerName);
            Assert.AreEqual(newName1, order2.CustomerName);
            Assert.AreEqual(order2, order1);
            Assert.AreEqual(TrackedState.Modified, tracked1.State);
        }

        [TestMethod]
        public void DirtyAwareCopyWorksPOCO()
        {
            _db.DirtyAwareObjectCopy = true;
            var order1 = _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var tracker = _db.GetTracker<TestOrderPOCO>();
            tracker.TryGet(order1.Id, out var tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            var newamtval = order1.Amount + 5;
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET Amount ='{newamtval}' WHERE OrderId={order1.Id}", -1, null);
            var oldnameval = order1.CustomerName;
            var newnameval = order1.CustomerName = order1.CustomerName + "_Changed!";
            Assert.AreEqual(TrackedState.Modified, tracked.State);

            _db.SqlRunQueryWithResults<TestOrderPOCO>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id));

            Assert.AreEqual(TrackedState.Modified, tracked.State);
            Assert.AreEqual(order1.CustomerName, newnameval);
            Assert.AreEqual(tracked.DirtyProperties.Count, 1);
            tracked.DirtyProperties.TryGetValue("CustomerName", out var dirty);
            Assert.AreEqual(dirty.OldValue, oldnameval);
            Assert.AreEqual(dirty.NewValue, newnameval);
            Assert.AreEqual(newamtval, order1.Amount);

            var ok = _db.RunSqlUpdate(order1, testorderupdate, false, -1, null,
                DBEngine.GetParameter(() => order1.Id),
                DBEngine.GetParameter(() => order1.CustomerName),
                DBEngine.GetParameter(() => order1.Amount),
                DBEngine.GetParameter(() => order1.RowVersion));

            Assert.IsTrue(ok);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

        }
        [TestMethod]
        public void DirtyAwareCopyWorksNO()
        {
            _db.DirtyAwareObjectCopy = true;
            var order1 = _db.SqlRunQueryWithResults<TestOrderNO>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var tracker = _db.GetTracker<TestOrderNO>();
            tracker.TryGet(order1.Id, out var tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            string notifyproperty = null;
            object notifyoldvalue = null;
            object notifynewvalue = null;
            int notifycount = 0;

            NotifierObject.PropertyUpdated += (s, e) =>
            {
                if (s.Equals(order1))
                {
                    notifyproperty = e.PropertyName;
                    notifyoldvalue = e.OldValue;
                    notifynewvalue = e.NewValue;
                    notifycount++;
                }
            };


            var newamtval = order1.Amount + 5;
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET Amount ='{newamtval}' WHERE OrderId={order1.Id}", -1, null);
            var oldnameval = order1.CustomerName;
            var newnameval = order1.CustomerName = order1.CustomerName + "_Changed!";
            Assert.AreEqual(TrackedState.Modified, tracked.State);

            Assert.AreEqual(newnameval, notifynewvalue);
            Assert.AreEqual(1, notifycount);


            _db.SqlRunQueryWithResults<TestOrderNO>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id));

            //notifycount will go from 1 -> 3 - there will be notifications for RowVersion and Amount
            Assert.AreEqual(3, notifycount);
            Assert.AreEqual(newamtval, order1.Amount);

            Assert.AreEqual(TrackedState.Modified, tracked.State);
            Assert.AreEqual(order1.CustomerName, newnameval);
            Assert.AreEqual(tracked.DirtyProperties.Count, 1);
            tracked.DirtyProperties.TryGetValue("CustomerName", out var dirty);
            Assert.AreEqual(dirty.OldValue, oldnameval);
            Assert.AreEqual(dirty.NewValue, newnameval);
            Assert.AreEqual(newamtval, order1.Amount);

            var ok = _db.RunSqlUpdate(order1, testorderupdate, false, -1, null,
                DBEngine.GetParameter(() => order1.Id),
                DBEngine.GetParameter(() => order1.CustomerName),
                DBEngine.GetParameter(() => order1.Amount),
                DBEngine.GetParameter(() => order1.RowVersion));

            Assert.AreEqual(4, notifycount);
            Assert.AreEqual("RowVersion", notifyproperty);

            Assert.IsTrue(ok);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);
        }
        [TestMethod]
        public void DirtyAwareCopyWorksINPC()
        {
            _db.DirtyAwareObjectCopy = true;
            var order1 = _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT TOP 1 * FROM dbo.TestOrders", false).First();
            var tracker = _db.GetTracker<TestOrderINPC>();
            tracker.TryGet(order1.Id, out var tracked);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);

            string notifyproperty = null;
            int notifycount = 0;

            order1.PropertyChanged += (s, e) =>
            {
                if (s.Equals(order1))
                {
                    notifyproperty = e.PropertyName;
                    notifycount++;
                }
            };


            var newamtval = order1.Amount + 5;
            _db.SqlRunStatement($"UPDATE dbo.TestOrders SET Amount ='{newamtval}' WHERE OrderId={order1.Id}", -1, null);
            var oldnameval = order1.CustomerName;
            var newnameval = order1.CustomerName = order1.CustomerName + "_Changed!";
            Assert.AreEqual(TrackedState.Modified, tracked.State);

            Assert.AreEqual(notifyproperty, "CustomerName");
            Assert.AreEqual(1, notifycount);


            _db.SqlRunQueryWithResults<TestOrderINPC>("SELECT * FROM dbo.TestOrders WHERE OrderId=@Id", false, -1, null,
                new SqlParameter("@Id", order1.Id));

            //notifycount will go from 1 -> 3 - there will be notifications for RowVersion and Amount
            Assert.AreEqual(3, notifycount);
            Assert.AreEqual(newamtval, order1.Amount);

            Assert.AreEqual(TrackedState.Modified, tracked.State);
            Assert.AreEqual(order1.CustomerName, newnameval);
            Assert.AreEqual(tracked.DirtyProperties.Count, 1);
            tracked.DirtyProperties.TryGetValue("CustomerName", out var dirty);
            Assert.AreEqual(dirty.OldValue, oldnameval);
            Assert.AreEqual(dirty.NewValue, newnameval);
            Assert.AreEqual(newamtval, order1.Amount);

            var ok = _db.RunSqlUpdate(order1, testorderupdate, false, -1, null,
                DBEngine.GetParameter(() => order1.Id),
                DBEngine.GetParameter(() => order1.CustomerName),
                DBEngine.GetParameter(() => order1.Amount),
                DBEngine.GetParameter(() => order1.RowVersion));

            //we just updated customername to be the same as what it is in the object so we won't get a notification for it
            //we will get a notification for RowVersion, though because it will have updated
            Assert.AreEqual(4, notifycount);
            Assert.AreEqual("RowVersion", notifyproperty);

            Assert.IsTrue(ok);
            Assert.AreEqual(TrackedState.Unchanged, tracked.State);
        }

        [TestMethod]
        public void LoadTestTablePOCO()
        {
            var items = _db.SqlRunQueryWithResults<TestTablePOCO>("SELECT TOP (2) * FROM dbo.TestTable", false);
            Assert.IsTrue(items.Count == 2);
        }
        [TestMethod]
        public void LoadTestTableNO()
        {
            var items = _db.SqlRunQueryWithResults<TestTableNO>("SELECT TOP (2) * FROM dbo.TestTable", false);
            Assert.IsTrue(items.Count == 2);
        }
        [TestMethod]
        public void LoadTableInStrangeColumnOrderToTestSequentialAccess()
        {
            _db.DefaultCommandBehavior = System.Data.CommandBehavior.SequentialAccess;
            var query = @"SELECT TOP (2) 
                       TinyIntCol,
                       SmallIntCol,
                       BitCol,
                       NtextCol,
                       GeographyCol,
                       HierarchyIdCol,
                       SqlVariantCol,
                       IntCol,
                       Id,
                       ModifiedDate,
                       BigIntCol,
                       DecimalCol,
                       VarcharCol,
                       NvarcharCol,
                       DateCol,
                       TimeCol,
                       MoneyCol,
                       FloatCol,
                       RealCol,
                       CharCol,
                       TextCol,
                       DateTimeOffsetCol,
                       BinaryCol,
                       VarbinaryCol,
                       DateTimeCol,
                       XmlCol 
                FROM dbo.TestTable";
            var items = _db.SqlRunQueryWithResults<TestTablePOCO>(query, false);
            Assert.AreEqual(2, items.Count);
        }
        [TestMethod]
        public void QueryWithoutRequiredPropertyThrows()
        {
            var query = @"SELECT TOP (10) 
                       TinyIntCol,
                       SmallIntCol,
                       BitCol,
                       NtextCol,
                       GeographyCol,
                       HierarchyIdCol,
                       SqlVariantCol,
                       IntCol,
                       Id,
                       ModifiedDate,
                       --BigIntCol, missing property
                       DecimalCol,
                       VarcharCol,
                       NvarcharCol,
                       DateCol,
                       TimeCol,
                       MoneyCol,
                       FloatCol,
                       RealCol,
                       CharCol,
                       TextCol,
                       DateTimeOffsetCol,
                       BinaryCol,
                       VarbinaryCol,
                       DateTimeCol,
                       XmlCol 
                FROM dbo.TestTable";
            string PropertyName = null;
            try
            {
                var items = _db.SqlRunQueryWithResults<TestTablePOCO>(query, false);

            }
            catch (DBEngineColumnRequiredException ex)
            {
                PropertyName = ex.PropertyName;
            }
            Assert.AreEqual("BigIntCol", PropertyName);
        }
        [TestMethod]
        public void ObjectWithWrongDataTypeThrows()
        {
            string PropertyName = null;
            try
            {
                var items = _db.SqlRunQueryWithResults<TestOrderBadProperty>("SELECT * FROM dbo.TestOrders", false);
            }
            catch (DBEngineMappingException ex)
            {
                PropertyName = ex.PropertyMapEntry.Property.Name;
            }
            Assert.AreEqual("CustomerName", PropertyName);
        }
        [TestMethod]
        public void ObjectWithOptionalAndIgnoreProperties()
        {
            var items = _db.SqlRunQueryWithResults<TestOrderWithAttributes>("SELECT * FROM dbo.TestOrders", false);
            Assert.AreEqual(2, items.Count);
        }
        [TestMethod]
        public void EnumMapping()
        {
            var items = _db.SqlRunQueryWithResults<MoarEnums>("SELECT CAST(1 as tinyint) AS TestEnum1, 2 as TestEnum2, 3 as TestEnum3", false);
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(TestEnum.Value1, items[0].TestEnum1);
            Assert.AreEqual(TestEnum2.Value2, items[0].TestEnum2);
            Assert.AreEqual(TestEnum.Value3, items[0].TestEnum3);

            items = _db.SqlRunQueryWithResults<MoarEnums>("SELECT '1' AS TestEnum1, 'Value2' as TestEnum2, N'Value3' as TestEnum3", false);
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(TestEnum.Value1, items[0].TestEnum1);
            Assert.AreEqual(TestEnum2.Value2, items[0].TestEnum2);
            Assert.AreEqual(TestEnum.Value3, items[0].TestEnum3);

            items = _db.SqlRunQueryWithResults<MoarEnums>("SELECT CAST(NULL AS varchar(100)) AS TestEnum1, 'value2' as TestEnum2, N'VALUE3' as TestEnum3", false);
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(TestEnum.NoValue, items[0].TestEnum1);
            Assert.AreEqual(TestEnum2.Value2, items[0].TestEnum2);
            Assert.AreEqual(TestEnum.Value3, items[0].TestEnum3);

            string propertyName = null;
            try
            {
                items = _db.SqlRunQueryWithResults<MoarEnums>("SELECT 50 AS TestEnum1, 'Bogus' as TestEnum2, N'VALUE3' as TestEnum3", false);
            }
            catch (DBEnginePostMappingException<MoarEnums> ex)
            {
                propertyName = ex.PropertyName;
            }
            Assert.AreEqual("TestEnum2", propertyName);
        }
        [TestMethod]
        public void TestChars()
        {
            var items = _db.SqlRunQueryWithResults<MoarChars>("SELECT 'a' AS TestChar1, N'b' as TestChar2, NULL as TestChar3, CAST('f' AS CHAR(1)) as TestChar4", false);
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual('a', items[0].TestChar1);
            Assert.AreEqual('b', items[0].TestChar2);
            Assert.AreEqual('\0', items[0].TestChar3);
            Assert.AreEqual('f', items[0].TestChar4);

            string propertyName = null;
            try
            {
                items = _db.SqlRunQueryWithResults<MoarChars>("SELECT 'ab' AS TestChar1, N'b' as TestChar2, NULL as TestChar3, CAST('f' AS CHAR(1)) as TestChar4", false);
            }
            catch (DBEnginePostMappingException<MoarChars> ex)
            {
                propertyName = ex.PropertyName;
            }
            Assert.AreEqual("TestChar1", propertyName);
        }
    }
}
