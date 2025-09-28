using MDDDataAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DBEngineUnitTests
{
    [TestClass]
    public class TrackedTests
    {
        [TestMethod]
        public void EndInitialization_ForInpcEntity_EnablesCachedDirtyChecking()
        {
            var entity = new InpcTrackable
            {
                Id = 1,
                RowVersion = new byte[] { 1 },
                Name = "Original",
                Amount = 10m
            };

            var tracked = new Tracked<InpcTrackable>(entity);

            Assert.AreEqual(DirtyCheckMode.Cached, tracked.DirtyCheckMode, "INotifyPropertyChanged entities should use cached dirty checking.");
            Assert.AreEqual(TrackedState.Unchanged, tracked.State, "Entity should start unchanged after initialization.");

            entity.Name = "Updated";

            Assert.AreEqual(TrackedState.Modified, tracked.State, "Changing a tracked property should mark the entity as modified.");
            var dirty = tracked.DirtyProperties.Single();
            Assert.AreEqual("Name", dirty.Key);
            Assert.AreEqual("Original", dirty.Value.OldValue);
            Assert.AreEqual("Updated", dirty.Value.NewValue);
        }

        [TestMethod]
        public void EndInitialization_WhenCalledTwice_ThrowsInvalidOperation()
        {
            var tracked = new Tracked<InpcTrackable>(key: 7, concurrency: new byte[] { 9 }, entity: new InpcTrackable());
            tracked.BeginInitialization(7, new byte[] { 9 }, out var entity);
            entity.Name = "Loaded";
            tracked.EndInitialization();

            Assert.ThrowsException<InvalidOperationException>(() => tracked.EndInitialization(), "Once initialization completes a second call should be rejected.");
        }

        [TestMethod]
        public void CopyValues_RespectsOptionalPropertySemantics()
        {
            var source = new OptionalTrackable
            {
                Id = 1,
                RowVersion = new byte[] { 3 },
                Required = 5,
                Optional = "interesting"
            };

            var target = new OptionalTrackable
            {
                Id = 1,
                RowVersion = new byte[] { 3 },
                Required = 1,
                Optional = null
            };

            var tracked = new Tracked<OptionalTrackable>(target);
            tracked.CopyValues(source, withinitialization: false);

            Assert.AreEqual(5, target.Required);
            Assert.AreEqual("interesting", target.Optional);

            source.Optional = null;
            target.Optional = "keep";
            tracked.CopyValues(source, withinitialization: false);

            Assert.AreEqual("keep", target.Optional, "Optional properties should not be overwritten by default values.");
        }

        [TestMethod]
        public void IsTrackable_ReturnsFalseWhenKeyAttributeMissing()
        {
            Assert.IsFalse(Tracked<Untrackable>.IsTrackable, "Types without a ListKey should not be considered trackable.");
        }

        private class InpcTrackable : INotifyPropertyChanged
        {
            private int _id;
            private byte[] _rowVersion;
            private string _name;
            private decimal _amount;

            [ListKey]
            public int Id
            {
                get => _id;
                set
                {
                    if (_id != value)
                    {
                        _id = value;
                        OnPropertyChanged();
                    }
                }
            }

            [ListConcurrency]
            public byte[] RowVersion
            {
                get => _rowVersion;
                set
                {
                    if (_rowVersion != value)
                    {
                        _rowVersion = value;
                        OnPropertyChanged();
                    }
                }
            }

            public string Name
            {
                get => _name;
                set
                {
                    if (_name != value)
                    {
                        _name = value;
                        OnPropertyChanged();
                    }
                }
            }

            public decimal Amount
            {
                get => _amount;
                set
                {
                    if (_amount != value)
                    {
                        _amount = value;
                        OnPropertyChanged();
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private class OptionalTrackable : INotifyPropertyChanged
        {
            private int _required;
            private string _optional;

            [ListKey]
            public int Id { get; set; }

            [ListConcurrency]
            public byte[] RowVersion { get; set; }

            public int Required
            {
                get => _required;
                set
                {
                    if (_required != value)
                    {
                        _required = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Required)));
                    }
                }
            }

            [DBOptional]
            public string Optional
            {
                get => _optional;
                set
                {
                    if (_optional != value)
                    {
                        _optional = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Optional)));
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private class Untrackable
        {
            public int Id { get; set; }
        }
    }

    [TestClass]
    public class TrackerTests
    {
        private DBEngine dbEngine;

        [TestInitialize]
        public void Setup()
        {
            dbEngine = new DBEngine("Server=.\\SQLEXPRESS;Database=master;Trusted_Connection=True;", "UnitTests");
        }

        [TestMethod]
        public void GetOrAdd_WithExistingEntity_ReusesTrackedInstance()
        {
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var entity = new InpcTrackable { Id = 1, RowVersion = new byte[] { 1 }, Name = "First", Amount = 10m };
            var tracked = tracker.GetOrAdd(ref entity);

            Assert.IsNotNull(tracked);
            Assert.AreSame(entity, GetTrackedEntity(tracked));

            var replacement = new InpcTrackable { Id = 1, RowVersion = new byte[] { 1 }, Name = "Second", Amount = 15m };
            var trackedAgain = tracker.GetOrAdd(ref replacement);

            Assert.AreSame(tracked, trackedAgain, "Tracker should reuse the existing tracked entry for the same key.");
            Assert.AreSame(entity, replacement, "The tracker should swap the provided reference with the tracked entity instance.");
        }

        [TestMethod]
        public void GetOrAdd_WhenRemoteConcurrencyChanges_MarksForReload()
        {
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var tracked = tracker.GetOrAdd(ref CreateEntity(1, 1));
            Assert.IsFalse(tracked.Initializing, "Newly loaded entities should have completed initialization.");

            var refreshed = tracker.GetOrAdd(1, new byte[] { 2 }, out var entity);

            Assert.AreSame(tracked, refreshed);
            Assert.IsTrue(tracked.Initializing, "A concurrency mismatch on an unchanged entity should trigger re-initialization.");
        }

        [TestMethod]
        public void GetOrAdd_WithModifiedEntityAndConcurrencyMismatch_WhenDirtyAwareDisabled_ThrowsConcurrencyMismatch()
        {
            dbEngine.DirtyAwareObjectCopy = false;
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var entity = CreateEntity(2, 1);
            tracker.GetOrAdd(ref entity);

            entity.Name = "Updated";

            var replacement = CreateEntity(2, 2);

            try
            {
                tracker.GetOrAdd(ref replacement);
                Assert.Fail("Expected GetOrAdd to throw when DirtyAwareObjectCopy is disabled.");
            }
            catch (Exception ex)
            {
                if (ex is DBEngineConcurrencyMismatchException mismatch)
                {
                    Assert.AreEqual(2, mismatch.KeyValue);
                    Assert.IsNull(mismatch.MismatchRecords, "Copy-based concurrency detection should not provide column detail.");
                }
                else
                {
                    Assert.Fail(ex.Message);
                    throw;
                }
            }
        }

        [TestMethod]
        public void GetOrAdd_WithModifiedEntityAndConcurrencyMismatch_WhenDirtyAwareMergeSucceeds_KeepsDirtyChanges()
        {
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var entity = CreateEntity(5, 1);
            var tracked = tracker.GetOrAdd(ref entity);
            var originalName = entity.Name;

            entity.Name = "Updated";

            var replacement = CreateEntity(5, 2);
            replacement.Name = originalName;

            var merged = tracker.GetOrAdd(ref replacement);

            Assert.AreSame(tracked, merged, "Tracker should reuse the existing tracked entry.");
            Assert.AreSame(entity, replacement, "The supplied reference should be replaced with the tracked entity.");
            Assert.AreEqual("Updated", entity.Name, "Dirty-aware merge should preserve compatible local edits.");
            Assert.AreEqual(TrackedState.Modified, tracked.State, "Entity should remain dirty after preserving local edits.");
            Assert.IsTrue(tracked.DirtyProperties.ContainsKey(nameof(InpcTrackable.Name)), "Name change should stay marked dirty.");
        }

        [TestMethod]
        public void GetOrAdd_WithModifiedEntityAndConcurrencyMismatch_WhenDirtyAwareDetectsConflict_ThrowsWithDetails()
        {
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var entity = CreateEntity(6, 1);
            var tracked = tracker.GetOrAdd(ref entity);

            entity.Name = "Updated";

            var replacement = CreateEntity(6, 2);
            replacement.Name = "Server";

            try
            {
                tracker.GetOrAdd(ref replacement);
                Assert.Fail("Expected dirty-aware merge to throw when conflicting changes exist.");
            }
            catch (Exception ex)
            {
                if (ex is DBEngineConcurrencyMismatchException mismatch)
                {
                    Assert.AreEqual(6, mismatch.KeyValue);
                    Assert.IsNotNull(mismatch.MismatchRecords, "Dirty-aware merge should report conflicting columns.");
                    var record = mismatch.MismatchRecords.Single();
                    Assert.AreEqual(nameof(InpcTrackable.Name), record.PropertyName);
                    Assert.AreEqual("Updated", record.AppValue);
                    Assert.AreEqual("Server", record.DBValue);
                }
                else
                {
                    Assert.Fail(ex.Message);
                    throw;
                }
            }

            Assert.AreEqual("Server", entity.Name, "Conflicting database value should overwrite local value.");
            Assert.AreEqual(TrackedState.Unchanged, tracked.State, "Entity should be reset after conflict handling.");
        }

        [TestMethod]
        public void GetOrAdd_WithKeyLookup_CreatesEntityAndAllowsInitialization()
        {
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var tracked = tracker.GetOrAdd(3, new byte[] { 5 }, out var entity);

            Assert.IsTrue(tracked.Initializing, "Entities loaded via key lookup should start initializing.");
            Assert.AreEqual(0, entity.Id, "Key assignment is performed by the caller during hydration.");

            entity.Id = 3;
            entity.RowVersion = new byte[] { 5 };
            tracked.EndInitialization();

            Assert.AreEqual(TrackedState.Unchanged, tracked.State);
        }

        [TestMethod]
        public async Task PruneInvalid_RemovesEntriesWhoseEntitiesWereCollected()
        {
            var tracker = new Tracker<InpcTrackable>(dbEngine);
            var entity = CreateEntity(4, 1);
            tracker.GetOrAdd(ref entity);

            var key = entity.Id;
            var weak = new WeakReference(entity);
            entity = null;

            var tries = 0;
            do
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100);
                tries++;
            } while (weak.IsAlive && tries < 5);

            Assert.IsFalse(weak.IsAlive, "Test setup failed to release the entity instance.");

            tracker.PruneInvalid();

            Assert.IsFalse(tracker.TryGet(key, out _), "PruneInvalid should discard entries whose entity has been garbage collected.");
        }

        private static InpcTrackable CreateEntity(int id, byte version)
        {
            return new InpcTrackable
            {
                Id = id,
                RowVersion = new byte[] { version },
                Name = "Entity" + id,
                Amount = id
            };
        }

        private static InpcTrackable GetTrackedEntity(Tracked<InpcTrackable> tracked)
        {
            tracked.TryGetEntity(out var entity);
            return entity;
        }

        private class InpcTrackable : INotifyPropertyChanged
        {
            private int _id;
            private byte[] _rowVersion;
            private string _name;
            private decimal _amount;

            [ListKey]
            public int Id
            {
                get => _id;
                set
                {
                    if (_id != value)
                    {
                        _id = value;
                        OnPropertyChanged();
                    }
                }
            }

            [ListConcurrency]
            public byte[] RowVersion
            {
                get => _rowVersion;
                set
                {
                    if (_rowVersion != value)
                    {
                        _rowVersion = value;
                        OnPropertyChanged();
                    }
                }
            }

            public string Name
            {
                get => _name;
                set
                {
                    if (_name != value)
                    {
                        _name = value;
                        OnPropertyChanged();
                    }
                }
            }

            public decimal Amount
            {
                get => _amount;
                set
                {
                    if (_amount != value)
                    {
                        _amount = value;
                        OnPropertyChanged();
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    [TestClass]
    public class ObjectFromReaderInternalTests
    {
        [TestMethod]
        public void EnsureConcurrencyProperty_ReturnsConcurrencyPropertyWhenPresent()
        {
            var db = CreateDbEngine();
            var entity = new TrackableForConcurrency { Id = 5, Version = new byte[] { 1 } };

            var property = InvokeEnsureConcurrency(db, entity, null);

            Assert.IsNotNull(property);
            Assert.AreEqual(nameof(TrackableForConcurrency.Version), property.Name);
        }

        [TestMethod]
        public void EnsureConcurrencyProperty_ThrowsWhenKeyMissing()
        {
            var db = CreateDbEngine();
            var entity = new TrackableForConcurrency();

            var ex = Assert.ThrowsException<Exception>(() => InvokeEnsureConcurrency(db, entity, null));
            StringAssert.Contains(ex.Message, "Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListKeyAttribute");
        }

        [TestMethod]
        public void EnsureConcurrencyProperty_ThrowsWhenConcurrencyMissing()
        {
            var db = CreateDbEngine();
            var entity = new TrackableWithoutConcurrency { Id = 3 };

            var ex = Assert.ThrowsException<Exception>(() => InvokeEnsureConcurrency(db, entity, null));
            StringAssert.Contains(ex.Message, "ListConcurrencyAttribute");
        }

        [TestMethod]
        public void ResolveReaderGetter_ReturnsSpecificGetterForKnownTypes()
        {
            var method = typeof(DBEngine).GetMethod("ResolveReaderGetter", BindingFlags.NonPublic | BindingFlags.Static);

            var dateTimeOffsetGetter = (MethodInfo)method.Invoke(null, new object[] { typeof(DateTimeOffset) });
            Assert.AreEqual(nameof(SqlDataReader.GetDateTimeOffset), dateTimeOffsetGetter.Name);

            var fallback = (MethodInfo)method.Invoke(null, new object[] { typeof(Uri) });
            Assert.AreEqual(nameof(SqlDataReader.GetFieldValue), fallback.Name, "Unsupported types should fall back to GetFieldValue<T>.");
        }

        [TestMethod]
        public void BuildStaticDateTimeFunc_AssignsCurrentTime()
        {
            var method = typeof(DBEngine).GetMethod("BuildStaticDateTimeFunc", BindingFlags.NonPublic | BindingFlags.Static);
            var property = typeof(EntityWithLoadedTime).GetProperty(nameof(EntityWithLoadedTime.LoadedAt));
            var action = (Action<SqlDataReader, object>)method.Invoke(null, new object[] { property });

            var entity = new EntityWithLoadedTime();
            action(null, entity);

            Assert.IsTrue(entity.LoadedAt > DateTime.MinValue);
            Assert.IsTrue((DateTime.UtcNow - entity.LoadedAt.ToUniversalTime()) < TimeSpan.FromSeconds(5), "Loaded time should be set to the current time.");
        }

        private static DBEngine CreateDbEngine()
        {
            return new DBEngine("Server=.\\SQLEXPRESS;Database=master;Trusted_Connection=True;", "UnitTests");
        }

        private static PropertyInfo InvokeEnsureConcurrency<T>(DBEngine db, T entity, PropertyInfo concurrency)
            where T : class
        {
            var method = typeof(DBEngine).GetMethod("EnsureConcurrencyProperty", BindingFlags.NonPublic | BindingFlags.Instance);
            var generic = method.MakeGenericMethod(typeof(T));
            try
            {
                return (PropertyInfo)generic.Invoke(db, new object[] { entity, concurrency });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        private class TrackableForConcurrency
        {
            [ListKey]
            public int Id { get; set; }

            [ListConcurrency]
            public byte[] Version { get; set; }
        }

        private class TrackableWithoutConcurrency
        {
            [ListKey]
            public int Id { get; set; }
        }

        private class EntityWithLoadedTime
        {
            public DateTime LoadedAt { get; set; }
        }
    }
}
