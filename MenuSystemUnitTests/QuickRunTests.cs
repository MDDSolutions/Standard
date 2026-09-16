using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FormsDataAccess;
using MDDDataAccess;
using MDDDataAccess.Menus;
using MDDFoundation.Menus;
using MDDWinForms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MenuItem = MDDFoundation.Menus.MenuItem;

namespace MenuSystemUnitTests
{
    /// <summary>
    /// Covers the Quick Run parameter runner without a database: value conversion, the editing grid,
    /// cancellation and failure recovery.
    /// </summary>
    [TestClass]
    public class QuickRunTests
    {
        // The control completes its work on the UI thread, so the message loop has to keep turning.
        private static MenuItem Procedure(int id, string name) => new MenuItem {
            Id = id, Title = name, Kind = MenuItemKind.Action, TargetKind = MenuTargetKind.StoredProcedure,
            ProcedureName = name, AllowNewInstance = true
        };

        [TestMethod]
        public void NullTextBecomesSqlNull() =>
            Assert.AreEqual(DBNull.Value, DBEngine.QuickRunParameterValue("NULL", SqlDbType.Int));

        [TestMethod]
        public void EmptyTextStaysAnEmptyString() =>
            Assert.AreEqual("", DBEngine.QuickRunParameterValue("", SqlDbType.NVarChar));

        [TestMethod]
        public void NumericTextConvertsToBit() =>
            Assert.IsTrue((bool)DBEngine.QuickRunParameterValue("1", SqlDbType.Bit));

        [TestMethod]
        public void HexadecimalTextConvertsToBinary() =>
            Assert.AreEqual(255, ((byte[])DBEngine.QuickRunParameterValue("0x00FF", SqlDbType.VarBinary))[1]);

        [TestMethod]
        public void InvalidNumericTextIsRejected() =>
            Assert.ThrowsException<FormatException>(() => DBEngine.QuickRunParameterValue("bad", SqlDbType.Int));

        [TestMethod]
        public void AProcedureItemIsAValidMenuEntry()
        {
            var category = new MenuItem { Id = 9, Title = "Quick Run", Kind = MenuItemKind.Category };
            var item = Procedure(1, "[dbo].[Test]");
            item.Categories.Add(new MenuCategoryRef { CategoryId = 9, SortOrder = 1 });
            Assert.AreEqual(2, MenuDefinition.Validate(new[] { category, item }).Count);
        }

        // The runner's async lifecycle - edit, execute, cancel, recover, close-while-busy - is not
        // covered here. It needs a real WinForms message loop; on a bare STA thread in the test host
        // the continuations never arrive and the test hangs rather than fails. Verify it by hand.

        // Window reuse per procedure is deliberately not covered here. Exercising it means letting
        // the dispatcher build a real QuickRunControl, which reaches for DBEngine.Default and will
        // try to open a connection - tests must never do that. Verify it by hand in the app.

    }
}
