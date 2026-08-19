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

        [TestMethod]
        public void ProcedureParameterPreservesMetadataForNullValues()
        {
            var metadata = new ProcedureParameter
            {
                name = "@Data",
                type_name = "varbinary",
                max_length = -1,
                precision = 0,
                scale = 0
            };

            var parameter = metadata.CreateSqlParameter(null);

            Assert.AreEqual("@Data", parameter.ParameterName);
            Assert.AreEqual(DBNull.Value, parameter.Value);
            Assert.AreEqual(SqlDbType.VarBinary, parameter.SqlDbType);
            Assert.AreEqual(-1, parameter.Size);
        }

        [TestMethod]
        public void ProcedureParameterConvertsUnicodeByteLengthToCharacterLength()
        {
            var metadata = new ProcedureParameter
            {
                name = "@Text",
                type_name = "nvarchar",
                max_length = 200
            };

            var parameter = metadata.CreateSqlParameter("hello");

            Assert.AreEqual(SqlDbType.NVarChar, parameter.SqlDbType);
            Assert.AreEqual(100, parameter.Size);
        }

        [TestMethod]
        public void ExpressionRunSqlUpdateOverloadHasAProcedureSpecificSignature()
        {
            var db = new DBEngine("server=.;database=tempdb;Trusted_Connection=true;", "DBEngineUnitTests");

            Func<ParameterTarget, System.Linq.Expressions.Expression<Func<object>>, bool> call =
                (target, expression) => db.RunSqlUpdate(target, "dbo.TestProcedure", -1, null, expression);

            Assert.IsNotNull(call);
        }

        private class ParameterTarget
        {
        }
    }
}
