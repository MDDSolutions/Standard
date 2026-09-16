using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MDDFoundation;
using System.Drawing;
using System.Threading;
using System.IO;

namespace MenuSystemUnitTests
{
    [TestClass]
    public class MDDFoundationTests
    {
        [TestMethod]
        public void PointSubtractGivesCorrectResult()
        {
            var p1 = new Point(5, 5);
            var p2 = new Point(10, 10);
            var x = p1.Subtract(p2);
            var xi = Convert.ToInt32(x);
            Assert.AreEqual(xi, 7);

            x = p2.Subtract(p1);
            xi = Convert.ToInt32(x);
            Assert.AreEqual(xi, 7);
        }
    }
}
