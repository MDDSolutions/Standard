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
    /// tracked objects instead of <see cref="System.Data.DataRow"/> instances.
    /// </summary>
    /// <typeparam name="T">Entity type being tracked.</typeparam>
    public class TrackedList<T> : INotifyPropertyChanged where T : class, new()
    {
        private readonly Tracker<T> tracker;
        private readonly SortableBindingList<T> bindingList;

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
            bindingList.ListChanged += BindingListOnListChanged;

            this.browserModeEnabled = browserModeEnabled;

            if (initialItems != null)
            {
                Load(initialItems);
            }
            else
            {
                OnDataSourceChanged();
                RaiseNavigationCanExecute();
            }

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

        public int Count => bindingList.Count;

        public int CurrentIndex
        {
            get => currentIndex >= 0 ? currentIndex + 1 : 0;
            set
            {
                if (value <= 0)
                {
                    SetCurrentIndexInternal(bindingList.Count > 0 ? 0 : -1);
                }
                else if (value - 1 < bindingList.Count)
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

        /// <summary>
        /// Delegate invoked when the current item is dirty and navigation is requested.
        /// Return <c>true</c> to save, <c>false</c> to discard without saving, or <c>null</c> to cancel navigation.
        /// </summary>
        public Func<TrackedEntity<T>, bool?> SaveChanges { get; set; } = _ => true;

        /// <summary>
        /// Forwarded persistence command from the current tracked entity.
        /// </summary>
        public ICommand SaveCommand => current?.SaveCommand;

        public ICommand NextCommand { get; private set; }
        public ICommand PreviousCommand { get; private set; }
        public ICommand FirstCommand { get; private set; }
        public ICommand LastCommand { get; private set; }
        public ICommand RemoveCurrentCommand { get; private set; }

        public void Load(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            ClearInternal();

            foreach (var item in entities)
            {
                var entity = item;
                EnsureTracked(ref entity);
                bindingList.Add(entity);
            }

            OnDataSourceChanged();

            if (bindingList.Count > 0)
            {
                SetCurrentIndexInternal(0, suppressPrompt: true);
            }
            else
            {
                SetCurrentIndexInternal(-1, suppressPrompt: true);
            }
        }

        public void Add(T entity, bool makeCurrent = true)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            if (makeCurrent && !PrepareForNavigation())
                return;

            var local = entity;
            var tracked = EnsureTracked(ref local);

            var existingIndex = FindIndexByKey(tracked.KeyValue);
            if (existingIndex >= 0)
            {
                if (!ReferenceEquals(bindingList[existingIndex], local))
                    bindingList[existingIndex] = local;

                if (makeCurrent)
                    SetCurrentIndexInternal(existingIndex, suppressPrompt: true);

                return;
            }

            bindingList.Add(local);

            if (makeCurrent)
            {
                SetCurrentIndexInternal(bindingList.Count - 1, suppressPrompt: true);
            }
            else
            {
                RaiseNavigationCanExecute();
            }
        }

        public bool RemoveCurrent()
        {
            if (currentIndex < 0 || currentIndex >= bindingList.Count)
                return false;

            RemoveAt(currentIndex);
            return true;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= bindingList.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var previousIndex = currentIndex;

            bindingList.RemoveAt(index);

            if (bindingList.Count == 0)
            {
                UpdateCurrent(-1);
                OnDataSourceChanged();
                return;
            }

            int targetIndex;
            if (index < previousIndex)
                targetIndex = previousIndex - 1;
            else if (index == previousIndex)
                targetIndex = Math.Min(previousIndex, bindingList.Count - 1);
            else
                targetIndex = previousIndex;

            SetCurrentIndexInternal(targetIndex, suppressPrompt: true);
        }

        public void Clear()
        {
            if (!PrepareForNavigation())
                return;

            ClearInternal();
            OnDataSourceChanged();
        }

        public void SetCurrent(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (!PrepareForNavigation())
                return;

            var local = entity;
            var tracked = EnsureTracked(ref local);
            SetCurrentInternal(ref local, tracked, allowAdd: true, suppressPrompt: true);
        }

        public void SetCurrentByKey(object key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!PrepareForNavigation())
                return;

            var index = FindIndexByKey(key);
            if (index >= 0)
            {
                SetCurrentIndexInternal(index, suppressPrompt: true);
                return;
            }

            if (!browserModeEnabled)
                throw new InvalidOperationException($"Key '{key}' is not part of the list.");

            if (!tracker.TryGet(key, out var tracked))
                throw new InvalidOperationException($"No tracked instance exists for key '{key}'.");

            if (!tracked.TryGetEntity(out var entity))
                throw new InvalidOperationException("Tracked entity has been released. Reload the entity before setting it as current.");

            var local = entity;
            SetCurrentInternal(ref local, tracked, allowAdd: true, suppressPrompt: true);
        }

        #endregion

        #region Internal helpers

        private void InitializeCommands()
        {
            NextCommand = new RelayCommand(Next, CanMoveNext);
            PreviousCommand = new RelayCommand(Previous, CanMovePrevious);
            FirstCommand = new RelayCommand(First, CanMoveFirst);
            LastCommand = new RelayCommand(Last, CanMoveLast);
            RemoveCurrentCommand = new RelayCommand(() => RemoveCurrent(), () => bindingList.Count > 0);
        }

        private void Next()
        {
            if (CanMoveNext())
                SetCurrentIndexInternal(currentIndex + 1);
        }

        private bool CanMoveNext() => currentIndex >= 0 && currentIndex < bindingList.Count - 1;

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

        private bool CanMoveFirst() => bindingList.Count > 0 && currentIndex != 0;

        private void Last()
        {
            if (CanMoveLast())
                SetCurrentIndexInternal(bindingList.Count - 1);
        }

        private bool CanMoveLast() => bindingList.Count > 0 && currentIndex != bindingList.Count - 1;

        private void BindingListOnListChanged(object sender, ListChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Count));
            RaiseNavigationCanExecute();
        }

        private bool PrepareForNavigation()
        {
            if (current == null)
                return true;

            if (current.State != TrackedState.Modified)
                return true;

            var decision = SaveChanges?.Invoke(current);

            if (decision == true)
            {
                var command = current.SaveCommand;
                if (command?.CanExecute(null) == true)
                    command.Execute(null);
                return true;
            }

            if (decision == false)
                return true;

            return false;
        }

        private void SetCurrentInternal(ref T entity, TrackedEntity<T> tracked, bool allowAdd, bool suppressPrompt)
        {
            var existingIndex = FindIndexByKey(tracked.KeyValue);
            if (existingIndex >= 0)
            {
                if (!ReferenceEquals(bindingList[existingIndex], entity))
                    bindingList[existingIndex] = entity;

                SetCurrentIndexInternal(existingIndex, suppressPrompt: suppressPrompt);
                return;
            }

            if (!allowAdd)
                throw new InvalidOperationException("The entity is not part of the list.");

            if (!browserModeEnabled)
                throw new InvalidOperationException("Cannot navigate to an entity outside the list when browser mode is disabled.");

            if (currentIndex >= 0 && currentIndex < bindingList.Count - 1)
                TrimBrowserHistory(currentIndex + 1);

            bindingList.Add(entity);
            OnDataSourceChanged();
            SetCurrentIndexInternal(bindingList.Count - 1, suppressPrompt: true);
        }

        private bool SetCurrentIndexInternal(int index, bool suppressPrompt = false)
        {
            if (currentIndex == index)
                return true;

            if (!suppressPrompt && !PrepareForNavigation())
                return false;

            UpdateCurrent(index);
            return true;
        }

        private void UpdateCurrent(int index)
        {
            var previous = current;

            if (previous != null)
            {
                previous.TrackedStateChanged -= CurrentTrackedStateChanged;
            }

            if (bindingList.Count == 0 || index < 0)
            {
                currentIndex = -1;
                current = null;
            }
            else
            {
                if (index >= bindingList.Count)
                    index = bindingList.Count - 1;

                currentIndex = index;
                var entity = bindingList[currentIndex];
                var tracked = EnsureTracked(ref entity);
                if (!ReferenceEquals(entity, bindingList[currentIndex]))
                    bindingList[currentIndex] = entity;

                current = tracked;
                current.TrackedStateChanged += CurrentTrackedStateChanged;
            }

            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(CurrentEntity));
            OnPropertyChanged(nameof(CurrentState));
            OnPropertyChanged(nameof(SaveCommand));

            if (!ReferenceEquals(previous, current))
            {
                CurrentChanged?.Invoke(this, new TrackedEntityChangedEventArgs<T>(previous, current));
            }

            RaiseNavigationCanExecute();
        }

        private void CurrentTrackedStateChanged(object sender, TrackedState e)
        {
            if (ReferenceEquals(sender, current))
            {
                OnPropertyChanged(nameof(CurrentState));
            }
        }

        private void ClearInternal()
        {
            if (current != null)
                current.TrackedStateChanged -= CurrentTrackedStateChanged;

            current = null;
            currentIndex = -1;
            bindingList.Clear();

            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(CurrentEntity));
            OnPropertyChanged(nameof(CurrentState));
            OnPropertyChanged(nameof(SaveCommand));
        }

        private void TrimBrowserHistory(int startIndex)
        {
            for (var i = bindingList.Count - 1; i >= startIndex; i--)
            {
                bindingList.RemoveAt(i);
            }
        }

        private TrackedEntity<T> EnsureTracked(ref T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var local = entity;
            var tracked = tracker.GetOrAdd(ref local);
            if (!ReferenceEquals(local, entity))
                entity = local;

            if (!tracked.TryGetEntity(out _))
                throw new InvalidOperationException("Tracked entity has no live reference.");

            return tracked;
        }

        private int FindIndexByKey(object key)
        {
            if (key == null)
                return -1;

            for (var i = 0; i < bindingList.Count; i++)
            {
                var candidate = bindingList[i];
                if (candidate == null)
                    continue;

                var candidateKey = TrackedEntity<T>.GetKeyValue(candidate);
                if (Equals(candidateKey, key))
                    return i;
            }

            return -1;
        }

        private void RaiseNavigationCanExecute()
        {
            (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FirstCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LastCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveCurrentCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
