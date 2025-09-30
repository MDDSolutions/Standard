using MDDFoundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MDDDataAccess
{
    /// <summary>
    /// View model that coordinates a collection of <see cref="TrackedEntity{T}"/> instances.
    /// The class mirrors the navigation surface of <see cref="TableViewModel"/> but operates on
    /// tracked objects instead of <see cref="System.Data.DataRow"/> instances.  It keeps the
    /// underlying entities in a <see cref="SortableBindingList{T}"/> so WinForms, WPF, or other
    /// UI frameworks can bind without being aware of the tracker infrastructure.
    /// </summary>
    /// <typeparam name="T">Entity type being tracked.</typeparam>
    public class TrackedList<T> : INotifyPropertyChanged where T : class, new()
    {
        private readonly Tracker<T> tracker;
        private readonly SortableBindingList<T> bindingList;
        private readonly List<TrackedEntity<T>> items;
        private readonly Dictionary<object, int> indexByKey;
        private readonly HashSet<object> dirtyKeys;

        private int currentIndex = -1;
        private TrackedEntity<T> current;
        private bool browserModeEnabled;

        /// <summary>
        /// Raised whenever a consumer should refresh the data source of their binding component.
        /// The payload is the <see cref="SortableBindingList{T}"/> maintained by this view model.
        /// </summary>
        public event EventHandler<DataSourceChangedEventArgs> DataSourceChanged;

        /// <summary>
        /// Raised when the current tracked entity changes.
        /// </summary>
        public event EventHandler<TrackedEntityChangedEventArgs<T>> CurrentChanged;

        public TrackedList(Tracker<T> tracker, IEnumerable<T> initialItems = null, bool browserModeEnabled = false)
        {
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            if (!TrackedEntity<T>.IsTrackable)
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not trackable.");

            bindingList = new SortableBindingList<T>();
            bindingList.ListChanged += (_, __) => OnPropertyChanged(nameof(Count));
            items = new List<TrackedEntity<T>>();
            indexByKey = new Dictionary<object, int>();
            dirtyKeys = new HashSet<object>();
            this.browserModeEnabled = browserModeEnabled;

            if (initialItems != null)
            {
                Load(initialItems);
            }

            OnDataSourceChanged();

            InitializeCommands();
        }

        #region Public Surface

        public SortableBindingList<T> DataSource => bindingList;

        public bool BrowserMode
        {
            get => browserModeEnabled;
            set
            {
                if (browserModeEnabled != value)
                {
                    browserModeEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Count => items.Count;

        public int CurrentIndex
        {
            get => currentIndex >= 0 ? currentIndex + 1 : 0;
            set
            {
                if (value <= 0)
                {
                    SetCurrentIndexInternal(items.Count > 0 ? 0 : -1);
                }
                else if (value - 1 < items.Count)
                {
                    SetCurrentIndexInternal(value - 1);
                }
            }
        }

        public TrackedEntity<T> Current => current;

        public TrackedState CurrentState => current?.State ?? TrackedState.Invalid;

        public T CurrentEntity
        {
            get
            {
                if (current != null && current.TryGetEntity(out var entity))
                    return entity;
                return null;
            }
        }

        public bool HasDirtyItems => dirtyKeys.Count > 0;

        public ICommand NextCommand { get; private set; }
        public ICommand PreviousCommand { get; private set; }
        public ICommand FirstCommand { get; private set; }
        public ICommand LastCommand { get; private set; }
        public ICommand RemoveCurrentCommand { get; private set; }

        public void Load(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            ClearInternal();

            foreach (var entity in entities)
            {
                AddInternal(entity, makeCurrent: false, browserAppend: false);
            }

            if (items.Count > 0)
            {
                SetCurrentIndexInternal(0);
            }
            else
            {
                SetCurrentIndexInternal(-1);
            }

            OnDataSourceChanged();
        }

        public void Add(T entity, bool makeCurrent = true)
        {
            AddInternal(entity, makeCurrent, browserAppend: true);
        }

        public bool RemoveCurrent()
        {
            if (currentIndex < 0 || currentIndex >= items.Count)
                return false;

            RemoveAt(currentIndex);
            if (items.Count == 0)
            {
                SetCurrentIndexInternal(-1);
            }
            else if (currentIndex >= items.Count)
            {
                SetCurrentIndexInternal(items.Count - 1);
            }
            else
            {
                SetCurrentIndexInternal(currentIndex);
            }
            return true;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var tracked = items[index];
            DetachTracked(tracked);
            items.RemoveAt(index);
            bindingList.RemoveAt(index);
            indexByKey.Remove(tracked.KeyValue);

            RebuildIndexMapFrom(index);
            OnPropertyChanged(nameof(Count));
        }

        public void Clear()
        {
            ClearInternal();
            SetCurrentIndexInternal(-1);
            OnDataSourceChanged();
        }

        public void SetCurrent(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var tracked = EnsureTracked(entity);

            if (indexByKey.TryGetValue(tracked.KeyValue, out var idx))
            {
                ReplaceTrackedAt(idx, tracked);
                SetCurrentIndexInternal(idx);
            }
            else
            {
                if (!browserModeEnabled)
                    throw new InvalidOperationException("Cannot navigate to an entity outside the list when browser mode is disabled.");

                if (currentIndex >= 0 && currentIndex < items.Count - 1)
                {
                    RemoveRange(currentIndex + 1, items.Count - currentIndex - 1);
                }

                var entityToAdd = tracked.TryGetEntity(out var canonical) ? canonical : throw new InvalidOperationException("Tracked entity has no live reference.");
                AttachTracked(tracked);
                items.Add(tracked);
                bindingList.Add(entityToAdd);
                indexByKey[tracked.KeyValue] = items.Count - 1;
                SetCurrentIndexInternal(items.Count - 1);
                OnPropertyChanged(nameof(Count));
            }
        }

        public void SetCurrentByKey(object key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            if (!indexByKey.TryGetValue(key, out var index))
            {
                if (!browserModeEnabled)
                    throw new InvalidOperationException($"Key '{key}' is not part of the list.");

                if (!tracker.TryGet(key, out var tracked))
                    throw new InvalidOperationException($"No tracked instance exists for key '{key}'.");

                if (!tracked.TryGetEntity(out var entity))
                    throw new InvalidOperationException("Tracked entity has been released.  Reload the entity before setting it as current.");

                SetCurrent(entity);
                return;
            }

            SetCurrentIndexInternal(index);
        }

        #endregion

        #region Internal helpers

        private void InitializeCommands()
        {
            NextCommand = new RelayCommand(Next, CanMoveNext);
            PreviousCommand = new RelayCommand(Previous, CanMovePrevious);
            FirstCommand = new RelayCommand(First, CanMoveFirst);
            LastCommand = new RelayCommand(Last, CanMoveLast);
            RemoveCurrentCommand = new RelayCommand(() => RemoveCurrent(), () => items.Count > 0);
        }

        private void Next()
        {
            if (CanMoveNext())
                SetCurrentIndexInternal(currentIndex + 1);
        }

        private bool CanMoveNext() => currentIndex >= 0 && currentIndex < items.Count - 1;

        private void Previous()
        {
            if (CanMovePrevious())
                SetCurrentIndexInternal(currentIndex - 1);
        }

        private bool CanMovePrevious() => currentIndex > 0;

        private void First()
        {
            if (CanMoveFirst())
                SetCurrentIndexInternal(0);
        }

        private bool CanMoveFirst() => items.Count > 0 && currentIndex != 0;

        private void Last()
        {
            if (CanMoveLast())
                SetCurrentIndexInternal(items.Count - 1);
        }

        private bool CanMoveLast() => items.Count > 0 && currentIndex != items.Count - 1;

        private void AddInternal(T entity, bool makeCurrent, bool browserAppend)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var tracked = EnsureTracked(entity);

            if (indexByKey.TryGetValue(tracked.KeyValue, out var existingIndex))
            {
                ReplaceTrackedAt(existingIndex, tracked);
                if (makeCurrent)
                    SetCurrentIndexInternal(existingIndex);
                return;
            }

            if (browserAppend && browserModeEnabled && currentIndex >= 0 && currentIndex < items.Count - 1)
            {
                RemoveRange(currentIndex + 1, items.Count - currentIndex - 1);
            }

            var canonical = tracked.TryGetEntity(out var entityRef)
                ? entityRef
                : throw new InvalidOperationException("Tracked entity has no live reference.");

            AttachTracked(tracked);

            items.Add(tracked);
            bindingList.Add(canonical);
            indexByKey[tracked.KeyValue] = items.Count - 1;

            OnPropertyChanged(nameof(Count));

            if (makeCurrent)
            {
                SetCurrentIndexInternal(items.Count - 1);
            }
        }

        private TrackedEntity<T> EnsureTracked(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var local = entity;
            var tracked = tracker.GetOrAdd(ref local);
            if (!tracked.TryGetEntity(out _))
                throw new InvalidOperationException("Tracked entity has no live reference.");
            return tracked;
        }

        private void AttachTracked(TrackedEntity<T> tracked)
        {
            tracked.TrackedStateChanged -= TrackedEntityStateChanged;
            tracked.TrackedStateChanged += TrackedEntityStateChanged;

            if (tracked.State == TrackedState.Modified)
                dirtyKeys.Add(tracked.KeyValue);
            else
                dirtyKeys.Remove(tracked.KeyValue);

            OnPropertyChanged(nameof(HasDirtyItems));
        }

        private void DetachTracked(TrackedEntity<T> tracked)
        {
            tracked.TrackedStateChanged -= TrackedEntityStateChanged;
            dirtyKeys.Remove(tracked.KeyValue);
            OnPropertyChanged(nameof(HasDirtyItems));
        }

        private void ReplaceTrackedAt(int index, TrackedEntity<T> newTracked)
        {
            var existing = items[index];
            if (ReferenceEquals(existing, newTracked))
                return;

            DetachTracked(existing);
            AttachTracked(newTracked);
            items[index] = newTracked;
            if (newTracked.TryGetEntity(out var entity))
            {
                bindingList[index] = entity;
            }
            indexByKey[newTracked.KeyValue] = index;
        }

        private void RemoveRange(int index, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var tracked = items[index];
                DetachTracked(tracked);
                indexByKey.Remove(tracked.KeyValue);
                items.RemoveAt(index);
                bindingList.RemoveAt(index);
            }
            RebuildIndexMapFrom(index);
            OnPropertyChanged(nameof(Count));
        }

        private void RebuildIndexMapFrom(int startIndex)
        {
            for (var i = startIndex; i < items.Count; i++)
            {
                var tracked = items[i];
                indexByKey[tracked.KeyValue] = i;
            }
        }

        private void ClearInternal()
        {
            foreach (var tracked in items)
            {
                DetachTracked(tracked);
            }

            items.Clear();
            bindingList.Clear();
            indexByKey.Clear();
            dirtyKeys.Clear();
            OnPropertyChanged(nameof(HasDirtyItems));
            OnPropertyChanged(nameof(Count));
        }

        private void SetCurrentIndexInternal(int index)
        {
            if (currentIndex == index)
                return;

            currentIndex = index;

            var previous = current;
            current = index >= 0 && index < items.Count ? items[index] : null;

            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(CurrentEntity));
            OnPropertyChanged(nameof(CurrentState));

            if (!ReferenceEquals(previous, current))
            {
                CurrentChanged?.Invoke(this, new TrackedEntityChangedEventArgs<T>(previous, current));
            }

            RaiseNavigationCanExecute();
        }

        private void RaiseNavigationCanExecute()
        {
            (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FirstCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LastCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveCurrentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void TrackedEntityStateChanged(object sender, TrackedState e)
        {
            if (sender is TrackedEntity<T> tracked)
            {
                switch (e)
                {
                    case TrackedState.Modified:
                        dirtyKeys.Add(tracked.KeyValue);
                        break;
                    case TrackedState.Unchanged:
                    case TrackedState.Invalid:
                        dirtyKeys.Remove(tracked.KeyValue);
                        break;
                }

                if (ReferenceEquals(tracked, current))
                {
                    OnPropertyChanged(nameof(CurrentState));
                }

                OnPropertyChanged(nameof(HasDirtyItems));
            }
        }

        private void OnDataSourceChanged()
        {
            DataSourceChanged?.Invoke(this, new DataSourceChangedEventArgs(bindingList));
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    public class DataSourceChangedEventArgs : EventArgs
    {
        public DataSourceChangedEventArgs(object dataSource)
        {
            DataSource = dataSource;
        }

        public object DataSource { get; }
    }

    public class TrackedEntityChangedEventArgs<T> : EventArgs where T : class, new()
    {
        public TrackedEntityChangedEventArgs(TrackedEntity<T> previous, TrackedEntity<T> current)
        {
            Previous = previous;
            Current = current;
        }

        public TrackedEntity<T> Previous { get; }
        public TrackedEntity<T> Current { get; }
    }
}
