using MDDFoundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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
        private TrackedEntity<T> currenttracked;
        private bool browserModeEnabled;
        private bool suppressNavigationPrompt = false;

        public event EventHandler<DataSourceChangedEventArgs> DataSourceChanged;
        public event EventHandler<TrackedEntityChangedEventArgs<T>> CurrentChanged;
        public Action<TrackedList<T>, Exception> TrackedListError { get; set; }

        public TrackedList(Tracker<T> tracker, IEnumerable<T> initialItems = null, bool browserModeEnabled = false)
        {
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            if (!TrackedEntity<T>.IsTrackable)
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not trackable.");

            bindingList = new SortableBindingList<T>();
            bindingList.ListChanged += BindingListOnListChanged;

            this.browserModeEnabled = browserModeEnabled;

            suppressNavigationPrompt = true;
            if (initialItems != null)
            {
                Load(initialItems);
            }
            else
            {
                OnDataSourceChanged();
                RaiseNavigationCanExecute();
            }
            suppressNavigationPrompt = false;

            //TrackedEntity<T>.TrackedEntityError += Currenttracked_TrackedEntityError;

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

        public int CurrentIndex => currentIndex >= 0 ? currentIndex + 1 : 0;

        public TrackedEntity<T> CurrentTracked => currenttracked;

        public TrackedState CurrentState => currenttracked?.State ?? TrackedState.Invalid;

        public T CurrentEntity => (currentIndex >= 0 && currentIndex < bindingList.Count)
            ? bindingList[currentIndex]
            : null;
        //public T CurrentEntity => currenttracked != null && currenttracked.TryGetEntity(out var entity) ? entity : null;

        public Func<TrackedEntity<T>, bool?> SaveChanges { get; set; } = null;
        public Func<TrackedEntity<T>, bool> PreparingForNavigation { get; set; } = _=> true;

        private AsyncDbCommand saveCommand;
        public AsyncDbCommand SaveCommand => saveCommand;

        public ICommand NextCommand { get; private set; }
        public ICommand PreviousCommand { get; private set; }
        public ICommand FirstCommand { get; private set; }
        public ICommand LastCommand { get; private set; }
        public ICommand RemoveCurrentCommand { get; private set; }

        // Synchronous load for initial population only (no async save possible)
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

        // Async load for scenarios where async navigation/save may be triggered
        public async Task LoadAsync(IEnumerable<T> entities)
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
                await SetCurrentIndexInternalAsync(0, suppressPrompt: true);
            else
                await SetCurrentIndexInternalAsync(-1, suppressPrompt: true);
        }

        public async Task AddAsync(T entity, bool makeCurrent = true)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (makeCurrent && !await PrepareForNavigationAsync()) return;
            var local = entity;
            var tracked = EnsureTracked(ref local);

            if (!makeCurrent && tracked == null) throw new Exception("New Records must be made current");


            int existingIndex = -1;
            if (tracked != null) 
                existingIndex = FindIndexByKey(tracked.KeyValue);
            if (existingIndex >= 0)
            {
                if (!ReferenceEquals(bindingList[existingIndex], local))
                    bindingList[existingIndex] = local;
                if (makeCurrent)
                    await SetCurrentIndexInternalAsync(existingIndex, suppressPrompt: true);
                return;
            }
            bindingList.Add(local);
            if (makeCurrent)
                await SetCurrentIndexInternalAsync(bindingList.Count - 1, suppressPrompt: true);
            else
                RaiseNavigationCanExecute();
        }

        public async Task<bool> RemoveCurrentAsync()
        {
            if (currentIndex < 0 || currentIndex >= bindingList.Count)
                return false;
            await RemoveAtAsync(currentIndex);
            return true;
        }

        public async Task RemoveAtAsync(int index)
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
            int targetIndex = index < previousIndex ? previousIndex - 1
                : index == previousIndex ? Math.Min(previousIndex, bindingList.Count - 1)
                : previousIndex;
            await SetCurrentIndexInternalAsync(targetIndex, suppressPrompt: true);
        }

        public async Task ClearAsync()
        {
            if (!await PrepareForNavigationAsync()) return;
            ClearInternal();
            OnDataSourceChanged();
        }

        public async Task SetCurrentAsync(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (!await PrepareForNavigationAsync()) return;
            var local = entity;
            var tracked = EnsureTracked(ref local);
            SetCurrentInternal(ref local, tracked, allowAdd: true, suppressPrompt: true);
        }

        public async Task SetCurrentByKeyAsync(object key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!await PrepareForNavigationAsync()) return;
            var index = FindIndexByKey(key);
            if (index >= 0)
            {
                await SetCurrentIndexInternalAsync(index, suppressPrompt: true);
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

        public async Task<bool> SetCurrentIndexAsync(int value)
        {
            if (value <= 0)
                return await SetCurrentIndexInternalAsync(bindingList.Count > 0 ? 0 : -1);
            else if (value - 1 < bindingList.Count)
                return await SetCurrentIndexInternalAsync(value - 1);
            return currentIndex == value;
        }

        #endregion

        #region Internal helpers

        private void InitializeCommands()
        {
            saveCommand = new AsyncDbCommand(
                async (dbe) =>
                {
                    if (currenttracked != null)
                    {
                        var cmd = currenttracked.SaveCommand as AsyncDbCommand;
                        if (cmd != null && cmd.CanExecute(tracker.DBEngine))
                        {


                            try { return await cmd.ExecuteAsync(tracker.DBEngine); }

                            catch (Exception ex)
                            {

                                if (TrackedListError != null) TrackedListError.Invoke(this, ex);

                                else throw;
                                return false;
                            }
                        }
                        return false;
                    }
                    else // new entity path
                    {
                        var cmd = TrackedEntity<T>.StaticSaveCommand as AsyncStaticDbCommand<T>;
                        var entity = CurrentEntity;
                        if (cmd != null && cmd.CanExecute(entity))
                        {
                            bool result = false;
                            try 
                            { 
                                result = await cmd.ExecuteAsync(tracker.DBEngine, entity); 
                            }
                            catch (Exception ex)
                            {
                                if (TrackedListError != null) TrackedListError.Invoke(this, ex);
                                else throw;
                                return false;
                            }
                            var key = TrackedEntity<T>.GetKeyValue(entity);
                            if (Foundation.IsDefaultOrNull(key))
                            {
                                var ex = new Exception("Save failed - entity did not get a key");
                                if (TrackedListError != null) TrackedListError.Invoke(this, ex);
                                else throw ex;
                                return false;
                            }
                            var tr = EnsureTracked(ref entity);
                            if (tr == null)
                            {
                                var ex = new Exception("Save may have succeeded but entity could not be added to the tracker");
                                if (TrackedListError != null) TrackedListError.Invoke(this, ex);
                                else throw ex;
                                return false;
                            }
                            // if EnsureTracked swapped the instance, keep the list consistent
                            if (!ReferenceEquals(entity, bindingList[currentIndex]))
                                bindingList[currentIndex] = entity;

                            currenttracked = tr;
                            currenttracked.TrackedStateChanged += CurrentTrackedStateChanged;
                            if (currenttracked.SaveCommand != null)
                                currenttracked.SaveCommand.CanExecuteChanged += SaveCommand_CanExecuteChanged;

                            OnPropertyChanged(nameof(CurrentTracked));
                            OnPropertyChanged(nameof(CurrentEntity));
                            OnPropertyChanged(nameof(CurrentState));
                            OnPropertyChanged(nameof(SaveCommand));

                            CurrentChanged?.Invoke(this, new TrackedEntityChangedEventArgs<T>(null, entity, currenttracked, entity));

                            RaiseNavigationCanExecute();
                            
                            return true;
                        }
                        return false;
                    }
                },
                () =>
                {
                    // enable when tracked row has something to save, OR when this is a new row and we have an insert handler + entity
                    if (currenttracked != null)
                        return currenttracked.SaveCommand?.CanExecute(tracker.DBEngine) == true;

                    return TrackedEntity<T>.StaticSaveCommand.CanExecute(CurrentEntity);
                }
            );
            NextCommand = new RelayCommand(() => _ = NextAsync(), CanMoveNext);
            PreviousCommand = new RelayCommand(() => _ = PreviousAsync(), CanMovePrevious);
            FirstCommand = new RelayCommand(() => _ = FirstAsync(), CanMoveFirst);
            LastCommand = new RelayCommand(() => _ = LastAsync(), CanMoveLast);
            RemoveCurrentCommand = new RelayCommand(() => _ = RemoveCurrentAsync(), () => bindingList.Count > 0);
        }

        public async Task NextAsync()
        {
            if (CanMoveNext())
                await SetCurrentIndexInternalAsync(currentIndex + 1);
        }

        private bool CanMoveNext() => currentIndex >= 0 && currentIndex < bindingList.Count - 1;

        public async Task PreviousAsync()
        {
            if (CanMovePrevious())
                await SetCurrentIndexInternalAsync(currentIndex - 1);
        }

        private bool CanMovePrevious() => currentIndex > 0;

        public async Task FirstAsync()
        {
            if (CanMoveFirst())
                await SetCurrentIndexInternalAsync(0);
        }

        private bool CanMoveFirst() => bindingList.Count > 0 && currentIndex != 0;

        public async Task LastAsync()
        {
            if (CanMoveLast())
                await SetCurrentIndexInternalAsync(bindingList.Count - 1);
        }

        private bool CanMoveLast() => bindingList.Count > 0 && currentIndex != bindingList.Count - 1;

        private void BindingListOnListChanged(object sender, ListChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Count));
            RaiseNavigationCanExecute();
        }

        public async Task<bool> PrepareForNavigationAsync()
        {
            if (suppressNavigationPrompt)
                return true;
            if (currenttracked == null)
                return true;

            var cancontinue = PreparingForNavigation?.Invoke(currenttracked) ?? true;
            if (!cancontinue) return false;

            if (currenttracked.State != TrackedState.Modified)
                return true;
            var decision = SaveChanges != null ? SaveChanges.Invoke(currenttracked) : true;
            if (decision == true)
            {
                var command = currenttracked.SaveCommand as AsyncDbCommand;
                if (command?.CanExecute(null) == true)
                {
                    try
                    {
                        return await command.ExecuteAsync(tracker.DBEngine);
                    }
                    catch (Exception ex)
                    {
                        if (TrackedListError != null)
                            TrackedListError.Invoke(this, ex);
                        else 
                            throw;
                        return false;
                    }
                }
                return true;
            }
            if (decision == false) return true;
            return false;
        }

        private void SetCurrentInternal(ref T entity, TrackedEntity<T> tracked, bool allowAdd, bool suppressPrompt)
        {
            int existingIndex = -1;
            if (tracked != null)
                existingIndex = FindIndexByKey(tracked.KeyValue);
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

        // Synchronous internal navigation for initial load only
        private bool SetCurrentIndexInternal(int index, bool suppressPrompt = false)
        {
            if (currentIndex == index)
                return true;
            if (!suppressPrompt && !PrepareForNavigationAsync().GetAwaiter().GetResult())
                return false;
            UpdateCurrent(index);
            return true;
        }

        // Async internal navigation for all other cases
        private async Task<bool> SetCurrentIndexInternalAsync(int index, bool suppressPrompt = false)
        {
            if (currentIndex == index)
                return true;
            if (!suppressPrompt && !await PrepareForNavigationAsync())
                return false;
            UpdateCurrent(index);
            return true;
        }

        private void UpdateCurrent(int index)
        {
            var previoustracked = currenttracked;

            if (previoustracked != null)
            {
                previoustracked.TrackedStateChanged -= CurrentTrackedStateChanged;
                
                if (previoustracked.SaveCommand != null)
                    previoustracked.SaveCommand.CanExecuteChanged -= SaveCommand_CanExecuteChanged;
            }

            T previousentity = null;
            if (currentIndex != -1)
                previousentity = bindingList[currentIndex];

            T currententity = null;

            if (bindingList.Count == 0 || index < 0)
            {
                currentIndex = -1;
                currenttracked = null;
            }
            else
            {
                if (index >= bindingList.Count)
                    index = bindingList.Count - 1;

                currentIndex = index;
                currententity = bindingList[currentIndex];
                currenttracked = EnsureTracked(ref currententity);
                if (!ReferenceEquals(currententity, bindingList[currentIndex]))
                    bindingList[currentIndex] = currententity;

                if (currenttracked != null)
                {
                    currenttracked.TrackedStateChanged += CurrentTrackedStateChanged;
                    if (currenttracked.SaveCommand != null)
                        currenttracked.SaveCommand.CanExecuteChanged += SaveCommand_CanExecuteChanged;   
                }          
            }

            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CurrentTracked));
            OnPropertyChanged(nameof(CurrentEntity));
            OnPropertyChanged(nameof(CurrentState));
            OnPropertyChanged(nameof(SaveCommand));

            if (!ReferenceEquals(previousentity, currententity))
                CurrentChanged?.Invoke(this, new TrackedEntityChangedEventArgs<T>(previoustracked, previousentity, currenttracked, currententity));

            RaiseNavigationCanExecute();
        }



        private void SaveCommand_CanExecuteChanged(object sender, EventArgs e)
        {
            saveCommand.RaiseCanExecuteChanged();
        }
        private void CurrentTrackedStateChanged(object sender, TrackedState e)
        {
            if (ReferenceEquals(sender, currenttracked))
            {
                OnPropertyChanged(nameof(CurrentState));
            }
        }
        private void Currenttracked_TrackedEntityError(object sender, Exception e)
        {
            var newex = new Exception($"TrackedEntity threw an error: {e.Message}", e);
            TrackedListError?.Invoke(this, newex);
        }
        private void ClearInternal()
        {
            if (currenttracked != null)
            {
                currenttracked.TrackedStateChanged -= CurrentTrackedStateChanged;
            }

            currenttracked = null;
            currentIndex = -1;
            bindingList.Clear();

            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CurrentTracked));
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

            // NEW: don't attach tracking if key is not set yet
            var key = TrackedEntity<T>.GetKeyValue(entity);
            if (Foundation.IsDefaultOrNull(key))
                return null;

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
        public TrackedEntityChangedEventArgs(TrackedEntity<T> previoustracked, T previousentity, TrackedEntity<T> currenttracked, T currententity)
        {
            PreviousTracked = previoustracked;
            PreviousEntity = previousentity;
            CurrentTracked = currenttracked;
            CurrentEntity = currententity;
        }

        public TrackedEntity<T> PreviousTracked { get; }
        public T PreviousEntity { get; }
        public TrackedEntity<T> CurrentTracked { get; }
        public T CurrentEntity { get; }
    }
}