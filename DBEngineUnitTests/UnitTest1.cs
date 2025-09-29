using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace DBEngineUnitTests
{
    [TestClass]
    public class NavigationPropertyTests
    {
        string ConnString = "server=MDD-SQL2022;database=tempdb;Trusted_Connection=true;";
        DBEngine _db;

        [TestInitialize]
        public void Initialize() 
        {
            var script = @"IF DB_ID() <> 2 
BEGIN
	SET NOEXEC ON;
	RAISERROR('You should be running this on tempdb',16,1);
END;
IF OBJECT_ID('dbo.OrderDetails') IS NOT NULL DROP TABLE OrderDetails;
IF OBJECT_ID('dbo.Item') IS NOT NULL DROP TABLE Item;

CREATE TABLE Item (
	ItemId INT PRIMARY KEY IDENTITY(101,1),
	ItemName VARCHAR(100) NOT NULL,
	modified_date DATETIME
);
GO
CREATE TRIGGER trgItem ON dbo.Item
FOR UPDATE
AS
UPDATE tgt SET tgt.modified_date = GETDATE()
FROM Inserted i
	JOIN dbo.Item tgt ON tgt.ItemId = i.ItemId
GO
CREATE TABLE OrderDetails (
	OrderDetailId INT PRIMARY KEY IDENTITY(201,1),
	OrderId INT,
	OrderQty INT NOT NULL,
	ItemId INT NOT NULL FOREIGN KEY REFERENCES dbo.Item (ItemId),
	modified_date DATETIME
);
GO
CREATE TRIGGER trgOrderDetails ON dbo.OrderDetails
FOR UPDATE
AS
UPDATE tgt SET tgt.modified_date = GETDATE()
FROM Inserted i
	JOIN dbo.OrderDetails tgt ON tgt.OrderDetailId = i.OrderDetailId
GO

INSERT dbo.Item (ItemName) VALUES ('Table'),('Chair');

INSERT dbo.OrderDetails (OrderId, OrderQty, ItemId)
SELECT 501, 2, ItemId FROM dbo.Item;";
            _db = new DBEngine(ConnString, "NavigationPropertyTesting") { AllowAdHoc = true, Tracking = ObjectTracking.IfAvailable };
            _db.ExecuteScript(script);
        }
        [TestMethod]
        public void NavigationPropertiesPopulateWhenTheQueryIncludesTheirColumns()
        {
            string query = "SELECT * FROM dbo.OrderDetails od JOIN dbo.Item i ON i.ItemId = od.ItemId";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.AreEqual(od.Count, 2);
            Assert.IsNotNull(od[0].OrderItem);
        }
        private class OrderDetails
        {
            [ListKey]
            public int OrderDetailId { get; set; }
            public int OrderId { get; set; }
            public int OrderQty { get; set; }
            public int ItemId { get; set; }
            public Item OrderItem { get; set; }
            [ListConcurrency]
            public DateTime? modified_date { get; set; }
        }
        private class Item
        {
            [ListKey]
            public int ItemId { get; set; }
            public string ItemName { get; set; }
            [ListConcurrency]
            public DateTime? modified_date { get; set; }
        }
    }
}
