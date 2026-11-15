using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DBEngineInMemoryTests
{
    // In-memory tests: no connection is opened and no database objects are used.
    [TestClass]
    public class NavigationRefreshTests
    {
        private Tracker<Scene> tracker;
        private Scene existing;
        private List<FileStatus> oldFiles;

        [TestInitialize]
        public void Initialize()
        {
            tracker = new Tracker<Scene>(CreateEngine());
            oldFiles = new List<FileStatus> { new FileStatus { Status = "Downloading" } };
            existing = new Scene { Id = 1, Version = 1, Files = oldFiles, UnmarkedFiles = oldFiles };
            tracker.GetOrAdd(ref existing, true);
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        public void HeaderRefreshInvalidatesFilesBeforeComputedStatusIsMerged(int version)
        {
            var incoming = new Scene { Id = 1, Version = version, Status = "Downloaded" };
            tracker.GetOrAddFromReader(ref incoming, Map(nameof(Scene.Id), nameof(Scene.Version), nameof(Scene.Status)));

            Assert.AreSame(existing, incoming);
            Assert.IsNull(existing.Files);
            Assert.AreEqual("Downloaded", existing.Status);
            Assert.AreSame(oldFiles, existing.UnmarkedFiles);
            Assert.AreEqual(TrackedState.Unchanged, tracker.GetOrAdd(ref existing).State);
        }

        [TestMethod]
        public void OrdinaryAttachmentPreservesOmittedNavigationEvenWhenInitializing()
        {
            var incoming = new Scene { Id = 1, Version = 1, Status = "Downloaded" };
            tracker.GetOrAdd(ref incoming, true);
            Assert.AreSame(oldFiles, incoming.Files);
        }

        [TestMethod]
        public void MappedNavigationReplacesTheExistingCollection()
        {
            var fresh = new List<FileStatus> { new FileStatus { Status = "Processed" } };
            var incoming = new Scene { Id = 1, Version = 1, Files = fresh };
            tracker.GetOrAddFromReader(ref incoming, Map(nameof(Scene.Files)));
            Assert.AreSame(fresh, incoming.Files);
            Assert.AreEqual("Processed", incoming.Status);
        }

        [TestMethod]
        public void MappedNullClearsExistingNavigation()
        {
            var incoming = new Scene { Id = 1, Version = 1, Status = "Created" };
            tracker.GetOrAddFromReader(ref incoming, Map(nameof(Scene.Files), nameof(Scene.Status)));
            Assert.IsNull(incoming.Files);
            Assert.AreEqual("Created", incoming.Status);
        }

        [TestMethod]
        public void MappedEmptyCollectionIsPreservedAsLoaded()
        {
            var empty = new List<FileStatus>();
            var incoming = new Scene { Id = 1, Version = 1, Files = empty };
            tracker.GetOrAddFromReader(ref incoming, Map(nameof(Scene.Files)));
            Assert.AreSame(empty, incoming.Files);
        }

        [TestMethod]
        public void UnmappedConstructorDefaultsDoNotCountAsSuppliedData()
        {
            var incoming = new Scene { Id = 1, Version = 1, Files = new List<FileStatus>() };
            tracker.GetOrAddFromReader(ref incoming, Map(nameof(Scene.Id)));
            Assert.IsNull(incoming.Files);
        }

        [TestMethod]
        public void InPlaceMaterializationClearsOnlyMarkedNavigationBeforeMapping()
        {
            DatabaseRefresh<Scene>.PrepareTarget(existing);
            existing.Status = "Downloaded";
            tracker.GetOrAddFromReader(ref existing, Map(nameof(Scene.Status)));
            Assert.IsNull(existing.Files);
            Assert.AreEqual("Downloaded", existing.Status);
            Assert.AreSame(oldFiles, existing.UnmarkedFiles);
        }

        [TestMethod]
        public void InvalidationAlsoWorksWithoutAConcurrencyToken()
        {
            var noVersionTracker = new Tracker<NoVersion>(CreateEngine());
            var first = new NoVersion { Id = 1, Files = oldFiles };
            noVersionTracker.GetOrAdd(ref first);
            var incoming = new NoVersion { Id = 1 };
            noVersionTracker.GetOrAddFromReader(ref incoming, new List<PropertyMapEntry>());
            Assert.AreSame(first, incoming);
            Assert.IsNull(incoming.Files);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ObjectFromReaderUsesTheRefreshPolicy(bool inPlace)
        {
            var engine = CreateEngine();
            Scene incoming = inPlace ? existing : null;
            PropertyInfo key = null;
            var map = Map(nameof(Scene.Id), nameof(Scene.Version), nameof(Scene.Status));
            map[0].MapAction = (reader, target) => ((Scene)target).Id = 1;
            map[1].MapAction = (reader, target) => ((Scene)target).Version = 1;
            map[2].MapAction = (reader, target) => ((Scene)target).Status = "Downloaded";
            // Precompiled actions need no reader: exercise the real materialization and
            // tracking path without connecting to SQL Server.
            engine.ObjectFromReader(null, ref map, ref key, ref incoming, ref tracker);
            Assert.AreSame(existing, incoming);
            Assert.IsNull(incoming.Files);
            Assert.AreEqual("Downloaded", incoming.Status);
            Assert.AreSame(oldFiles, incoming.UnmarkedFiles);
        }

        private static List<PropertyMapEntry> Map(params string[] names)
            => names.Select(name => new PropertyMapEntry { Property = typeof(Scene).GetProperty(name) }).ToList();

        private static DBEngine CreateEngine()
            => new DBEngine("Data Source=unused;Initial Catalog=unused", "NavigationRefreshTests");

        public class Scene
        {
            [ListKey] public int Id { get; set; }
            [ListConcurrency] public int Version { get; set; }
            [DBInvalidateOnRefresh] public IList<FileStatus> Files { get; set; }
            public IList<FileStatus> UnmarkedFiles { get; set; }
            private string status;
            [DBOptional]
            public string Status
            {
                get
                {
                    if (Files != null) status = string.Join(",", Files.Select(f => f.Status));
                    return status;
                }
                set => status = value;
            }
        }

        public class NoVersion
        {
            [ListKey] public int Id { get; set; }
            [DBInvalidateOnRefresh] public IList<FileStatus> Files { get; set; }
        }

        public class FileStatus
        {
            public string Status { get; set; }
        }
    }
}
