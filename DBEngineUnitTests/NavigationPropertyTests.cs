using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DBEngineUnitTests
{
    [TestClass]
    public class NavigationPropertyTests
    {
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
IF OBJECT_ID('dbo.Category') IS NOT NULL DROP TABLE Category;
IF OBJECT_ID('dbo.OrderHeader') IS NOT NULL DROP TABLE OrderHeader;


CREATE TABLE OrderHeader (
	OrderID INT PRIMARY KEY IDENTITY(50,1),
	OrderDate DATE NOT NULL,
	modified_date DATETIME
);
GO
CREATE TRIGGER trgOrderHeader ON dbo.OrderHeader
FOR UPDATE
AS
UPDATE tgt SET modified_date = GETDATE()
FROM Inserted i
	JOIN dbo.OrderHeader tgt ON tgt.OrderID = i.OrderID
GO

CREATE TABLE Category (
        CategoryId INT PRIMARY KEY IDENTITY(301,1),
        CategoryName VARCHAR(100) NOT NULL,
        modified_date DATETIME
);
GO
CREATE TRIGGER trgCategory ON dbo.Category
FOR UPDATE
AS
UPDATE tgt SET tgt.modified_date = GETDATE()
FROM Inserted i
        JOIN dbo.Category tgt ON tgt.CategoryId = i.CategoryId
GO
CREATE TABLE Item (
        ItemId INT PRIMARY KEY IDENTITY(101,1),
        ItemName VARCHAR(100) NOT NULL,
        CategoryId INT NOT NULL FOREIGN KEY REFERENCES dbo.Category (CategoryId),
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
	OrderId INT NOT NULL FOREIGN KEY REFERENCES dbo.OrderHeader (OrderID),
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

INSERT dbo.Category (CategoryName) VALUES ('Furniture'),('Office');

INSERT dbo.Item (ItemName, CategoryId)
SELECT 'Table', CategoryId FROM dbo.Category WHERE CategoryName = 'Furniture';
INSERT dbo.Item (ItemName, CategoryId)
SELECT 'Chair', CategoryId FROM dbo.Category WHERE CategoryName = 'Office';

INSERT dbo.OrderHeader (OrderDate) VALUES ('2025-11-05');
DECLARE @OrderID INT = SCOPE_IDENTITY();

INSERT dbo.OrderDetails (OrderId, OrderQty, ItemId)
SELECT @OrderID, 2, ItemId FROM dbo.Item;";
            _db = new DBEngine(Global.ConnString, "NavigationPropertyTesting") { AllowAdHoc = true, Tracking = ObjectTracking.IfAvailable };
            _db.ExecuteScript(script);
        }
        [TestMethod]
        public void NavigationPropertiesPopulateWhenTheQueryIncludesTheirColumns()
        {
            string query = "SELECT * FROM dbo.OrderDetails od JOIN dbo.Item i ON i.ItemId = od.ItemId";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.AreEqual(od.Count, 2);

            var odtracker = _db.GetTracker<OrderDetails>();
            Assert.IsNotNull(odtracker);
            Assert.AreEqual(odtracker.Count, 2);

            Assert.IsNotNull(od[0].OrderItem);
            var ittracker = _db.GetTracker<Item>();
            Assert.IsNotNull(ittracker);
            Assert.AreEqual(ittracker.Count, 2);
        }
        [TestMethod]
        public void NavigationPropertiesRemainNullWhenTheQueryDoesNotIncludeTheirColumns()
        {
            string query = "SELECT * FROM dbo.OrderDetails";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.AreEqual(od.Count, 2);
            Assert.IsTrue(od.All(x => x.OrderItem == null));
        }

        [TestMethod]
        public void NavigationPropertiesAreNotInstantiatedWhenJoinedColumnsAreNull()
        {
            string query = @"SELECT od.OrderDetailId, od.OrderId, od.OrderQty, od.ItemId, od.modified_date,
                                    child.ItemId, child.ItemName, child.CategoryId, child.modified_date
                             FROM dbo.OrderDetails od
                             LEFT JOIN dbo.Item child ON 1 = 0
                             ORDER BY od.OrderDetailId";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.AreEqual(od.Count, 2);
            Assert.IsTrue(od.All(x => x.OrderItem == null));
        }

        [TestMethod]
        public void NavigationPropertyConcurrencyColumnsAreMappedIndependently()
        {
            _db.SqlRunStatement("UPDATE dbo.Item SET ItemName = ItemName");
            Thread.Sleep(50);
            _db.SqlRunStatement("UPDATE dbo.OrderDetails SET OrderQty = OrderQty");


            string query = "SELECT * FROM dbo.OrderDetails od JOIN dbo.Item i ON i.ItemId = od.ItemId ORDER BY od.OrderDetailId";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.IsTrue(od.All(x => x.modified_date.HasValue));
            Assert.IsTrue(od.All(x => x.OrderItem != null && x.OrderItem.modified_date.HasValue));
            Assert.IsTrue(od.All(x => x.OrderItem.modified_date < x.modified_date));
        }

        [TestMethod]
        public void NestedNavigationPropertiesPopulateWhenColumnsAreProvided()
        {
            string query = @"SELECT *
                             FROM dbo.OrderDetails od
                             JOIN dbo.Item i ON i.ItemId = od.ItemId
                             JOIN dbo.Category c ON c.CategoryId = i.CategoryId
                             ORDER BY od.OrderDetailId";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.AreEqual(od.Count, 2);
            Assert.IsTrue(od.All(x => x.OrderItem != null));
            Assert.IsTrue(od.All(x => x.OrderItem.Category != null));
            var categories = od.Select(x => x.OrderItem.Category.CategoryName).ToList();
            CollectionAssert.AreEquivalent(new[] { "Furniture", "Office" }, categories);
        }
        [TestMethod]
        public void NestedListNavigationPropertiesPopulateAndSmartAdd()
        {
            string query = @"SELECT * 
                             FROM dbo.OrderHeader h
	                            JOIN dbo.OrderDetails d ON d.OrderId = h.OrderID";
            var ord = _db.SqlRunQueryWithResults<OrderHeader>(query, false);
            Assert.IsTrue(ord.Count == 1);
            Assert.IsTrue(ord[0].Details.Count == 2);
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
            public override string ToString()
            {
                return $"OrderDetailID: {OrderDetailId}, ItemId: {ItemId}";
            }
        }
        private class OrderHeader
        {
            [ListKey] public int OrderID { get; set; }
            public DateTime OrderDate { get; set; }
            [ListConcurrency] public DateTime modified_date { get; set; }
            public List<OrderDetails> Details { get; set; }
            public override string ToString()
            {
                return $"OrderID: {OrderID}";
            }
        }
        private class Item
        {
            [ListKey]
            public int ItemId { get; set; }
            public string ItemName { get; set; }
            public int CategoryId { get; set; }
            [ListConcurrency]
            public DateTime? modified_date { get; set; }
            public Category Category { get; set; }
            public override string ToString()
            {
                return $"ItemId: {ItemId}";
            }
        }
        private class Category
        {
            [ListKey]
            public int CategoryId { get; set; }
            public string CategoryName { get; set; }
            [ListConcurrency]
            public DateTime? modified_date { get; set; }
            public override string ToString()
            {
                return $"CategoryId: {CategoryId}, CategoryName: {CategoryName}";
            }
        }
    }
}
