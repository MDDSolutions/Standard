using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data;

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

        [TestMethod]
        public void GetParameterUsesDeclaredTypeForNullValues()
        {
            byte[] bytes = null;
            int? number = null;
            string text = null;

            var binaryParameter = DBEngine.GetParameter(() => bytes);
            var integerParameter = DBEngine.GetParameter(() => number);
            var textParameter = DBEngine.GetParameter(() => text);

            Assert.AreEqual("@bytes", binaryParameter.ParameterName);
            Assert.AreEqual(DBNull.Value, binaryParameter.Value);
            Assert.AreEqual(SqlDbType.Binary, binaryParameter.SqlDbType);
            Assert.AreEqual(SqlDbType.Int, integerParameter.SqlDbType);
            Assert.AreEqual(SqlDbType.VarChar, textParameter.SqlDbType);
        }
    }
}
