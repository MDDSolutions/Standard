using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace DBEngineUnitTests
{
    [TestClass]
    public class SimpleTests
    {
        [TestMethod]
        public void IsProcedureWorks()
        {
            string sql = null;
            Assert.IsFalse(DBEngine.IsProcedure(sql));
            sql = "SELECT * FROM dbo.MyTable;";
            Assert.IsFalse(DBEngine.IsProcedure(sql));
            sql = "dbo.MyProcedure";
            Assert.IsTrue(DBEngine.IsProcedure(sql));
        }
    }
}
