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

        [TestMethod]
        public void FoundationAppPathsRecognizesCurrentLauncherLayout()
        {
            var root = Path.Combine(Path.GetTempPath(), "AppManagerPathTest");
            var paths = FoundationAppPaths.Resolve(Path.Combine(root, "current"));

            Assert.IsTrue(paths.IsLauncherManaged);
            Assert.AreEqual(Path.GetFullPath(root), paths.AppRootDirectory);
            Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "config"), paths.ConfigDirectory);
            Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "logs"), paths.LogDirectory);
        }

        [TestMethod]
        public void FoundationAppPathsStillRecognizesVersionedLauncherLayout()
        {
            var root = Path.Combine(Path.GetTempPath(), "AppManagerPathTest");
            var paths = FoundationAppPaths.Resolve(Path.Combine(root, "versions", "2026.09.18.12.00"));

            Assert.IsTrue(paths.IsLauncherManaged);
            Assert.AreEqual(Path.GetFullPath(root), paths.AppRootDirectory);
        }

        [TestMethod]
        public void FoundationAppPathsLeavesNormalLayoutAlone()
        {
            var appDirectory = Path.Combine(Path.GetTempPath(), "OrdinaryApplication");
            var paths = FoundationAppPaths.Resolve(appDirectory);

            Assert.IsFalse(paths.IsLauncherManaged);
            Assert.AreEqual(Path.GetFullPath(appDirectory), paths.AppRootDirectory);
            Assert.AreEqual(Path.GetFullPath(appDirectory), paths.ConfigDirectory);
            Assert.AreEqual(Path.GetFullPath(appDirectory), paths.LogDirectory);
        }
    }
}
