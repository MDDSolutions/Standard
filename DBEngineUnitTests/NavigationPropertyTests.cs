﻿using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

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
IF OBJECT_ID('dbo.Category') IS NOT NULL DROP TABLE Category;

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

INSERT dbo.Category (CategoryName) VALUES ('Furniture'),('Office');

INSERT dbo.Item (ItemName, CategoryId)
SELECT 'Table', CategoryId FROM dbo.Category WHERE CategoryName = 'Furniture';
INSERT dbo.Item (ItemName, CategoryId)
SELECT 'Chair', CategoryId FROM dbo.Category WHERE CategoryName = 'Office';

INSERT dbo.OrderDetails (OrderId, OrderQty, ItemId)
SELECT 501, 2, ItemId FROM dbo.Item;

UPDATE dbo.Category SET CategoryName = CategoryName;
UPDATE dbo.Item SET ItemName = ItemName;
UPDATE dbo.OrderDetails SET OrderQty = OrderQty;";
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
            string query = "SELECT * FROM dbo.OrderDetails od JOIN dbo.Item i ON i.ItemId = od.ItemId ORDER BY od.OrderDetailId";
            var od = _db.SqlRunQueryWithResults<OrderDetails>(query, false);
            Assert.IsNotNull(od);
            Assert.IsTrue(od.All(x => x.modified_date.HasValue));
            Assert.IsTrue(od.All(x => x.OrderItem != null && x.OrderItem.modified_date.HasValue));
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
            public int CategoryId { get; set; }
            [ListConcurrency]
            public DateTime? modified_date { get; set; }
            public Category Category { get; set; }
        }
        private class Category
        {
            [ListKey]
            public int CategoryId { get; set; }
            public string CategoryName { get; set; }
            [ListConcurrency]
            public DateTime? modified_date { get; set; }
        }
    }
}
