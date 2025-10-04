using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DBEngineUnitTests
{
    [TestClass]
    public class TrackedListTests
    {
        private DBEngine dbEngine;
        private Tracker<TestEntity> tracker;

        [TestInitialize]
        public void SetUp()
        {
            dbEngine = new DBEngine(Global.ConnString, "UnitTests");
            tracker = new Tracker<TestEntity>(dbEngine);
        }

        [TestMethod]
        public void Load_PopulatesBindingListAndSetsCurrentEntity()
        {
            var trackedList = new TrackedList<TestEntity>(tracker);
            var dataSourceEvents = 0;
            object latestDataSource = null;
            trackedList.DataSourceChanged += (sender, args) =>
            {
                dataSourceEvents++;
                latestDataSource = args.DataSource;
            };

            var initialItems = new[]
            {
                CreateEntity(1, name: "First"),
                CreateEntity(2, name: "Second")
            };

            trackedList.LoadAsync(initialItems).GetAwaiter().GetResult();

            Assert.AreEqual(1, dataSourceEvents, "Load should raise a single DataSourceChanged event.");
            Assert.AreSame(trackedList.DataSource, latestDataSource, "The binding list should be provided in the change event.");
            Assert.AreEqual(2, trackedList.Count, "Both items should be present after loading.");
            Assert.IsNotNull(trackedList.CurrentEntity, "The first item should become current.");
            Assert.AreEqual(1, trackedList.CurrentEntity.Id);
            Assert.AreEqual(TrackedState.Unchanged, trackedList.CurrentState, "Freshly loaded entities should be unchanged.");
        }

        [TestMethod]
        public async Task SetCurrent_InBrowserMode_AppendsNewEntityAndTrimsForwardHistory()
        {
            var items = new[]
            {
                CreateEntity(1),
                CreateEntity(2),
                CreateEntity(3)
            };

            var trackedList = new TrackedList<TestEntity>(tracker, items);
            trackedList.BrowserMode = true;
            await trackedList.SetCurrentIndexAsync(2); // move to the second entry (1-based index)

            var fourth = CreateEntity(4);

            await trackedList.SetCurrentAsync(fourth);

            CollectionAssert.AreEqual(new List<int> { 1, 2, 4 }, trackedList.DataSource.Select(e => e.Id).ToList(),
                "Browser-mode navigation should trim items ahead of the previous position and append the new entity.");
            Assert.AreEqual(4, trackedList.CurrentEntity.Id, "The new entity should be the current item.");
        }

        [TestMethod]
        public void Navigation_WhenSaveChangesCancels_KeepsCurrentItem()
        {
            var items = new[]
            {
                CreateEntity(1, name: "Original"),
                CreateEntity(2, name: "Second")
            };

            var trackedList = new TrackedList<TestEntity>(tracker, items);
            var prompts = 0;
            trackedList.SaveChanges = tracked =>
            {
                prompts++;
                return null; // cancel navigation
            };

            trackedList.CurrentEntity.Name = "Updated";
            Assert.AreEqual(TrackedState.Modified, trackedList.CurrentState, "Changing the entity should mark it dirty.");

            Assert.IsTrue(trackedList.NextCommand.CanExecute(null), "Next navigation should be available before cancellation.");
            trackedList.NextCommand.Execute(null);

            Assert.AreEqual(1, prompts, "SaveChanges should have been invoked once when navigating away from a dirty entity.");
            Assert.AreEqual(1, trackedList.CurrentEntity.Id, "Navigation should stay on the original item when cancelled.");
        }

        private static TestEntity CreateEntity(int id, byte version = 1, string name = null)
        {
            var entity = new TestEntity
            {
                Id = id,
                RowVersion = new byte[] { version },
                Name = name ?? $"Name {id}"
            };
            entity.Initializing = false;
            return entity;
        }

        private class TestEntity : NotifierObject
        {
            private int id;
            private byte[] rowVersion;
            private string name;

            [ListKey]
            public int Id
            {
                get => id;
                set => SetProperty(ref id, value);
            }

            [ListConcurrency]
            public byte[] RowVersion
            {
                get => rowVersion;
                set => SetProperty(ref rowVersion, value);
            }

            public string Name
            {
                get => name;
                set => SetProperty(ref name, value);
            }
        }
    }
}