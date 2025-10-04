using MDDFoundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;


namespace MDDDataAccess
{
    public class TrackedEntity<T> where T : class, new()
    {
        // ---------- Static metadata ----------
        private static bool? istrackable = null;
        public static bool IsTrackable 
        {
            get
            {
                if (!istrackable.HasValue)
                    Initialize();
                return istrackable.Value;
            } 
        }
        public static PropertyInfo KeyProperty { get; private set; }
        public static string KeyDBName { get; private set; }
        public static bool HasConcurrency { get; private set; }
        public static bool SupportsDirtyAwareCopy { get; private set; }
        public static PropertyInfo ConcurrencyProperty { get; private set; }
        public static string ConcurrencyDBName { get; private set; }
        // Compiled delegates for speed
        public static Func<T, object> GetKeyValue;
        public static Action<T, object> SetKeyValue;
        public static Func<T, object> GetConcurrencyValue;
        public static Action<T, object> SetConcurrencyValue;

        private static Dictionary<string, PropertyDelegateInfo<T>> _allpropertydelegates { get; set; }
        public static Dictionary<string, PropertyDelegateInfo<T>> GetAllPropertyDelegates()
        {
            var result = new Dictionary<string, PropertyDelegateInfo<T>>();
            foreach (var item in _allpropertydelegates)
            {
                if (item.Value.PublicSetter)
                    result.Add(item.Key, item.Value);
                else
                    result.Add(item.Key,item.Value.GetSanitized());
            }
            return result;
        }
        private static readonly ConditionalWeakTable<T, TrackedEntity<T>> _entityToTracked = new ConditionalWeakTable<T, TrackedEntity<T>>();
        private static readonly object initLock = new object();
        public static void Initialize(Type keyAttributeType = null, Type concurrencyAttributeType = null)
        {
            if (istrackable.HasValue)
                return; // already initialized

            lock (initLock)
            {
                if (istrackable.HasValue)
                    return; // already initialized

                if (keyAttributeType == null)
                    keyAttributeType = typeof(ListKeyAttribute);
                if (concurrencyAttributeType == null)
                    concurrencyAttributeType = typeof(ListConcurrencyAttribute);

                var type = typeof(T);
                SupportsDirtyAwareCopy = type.GetCustomAttributes(typeof(DirtyAwareCopyAttribute), true).Any();

                var updcmd = type.GetCustomAttribute<DBUpsertAttribute>();
                if (updcmd != null) UpdateCommand = updcmd.UpsertProcName;

                // find key property
                KeyProperty = type.GetProperties().FirstOrDefault(p => p.GetCustomAttributes(keyAttributeType, true).Any());
                if (KeyProperty == null)
                {
                    istrackable = false;
                    return;
                }
                var keyAttr = KeyProperty.GetCustomAttributes(typeof(DBNameAttribute), true).FirstOrDefault() as DBNameAttribute;
                KeyDBName = keyAttr?.DBName ?? KeyProperty.Name;

                var entityParam = Expression.Parameter(typeof(T), "entity");
                var keyAccess = Expression.Property(entityParam, KeyProperty);
                var keyConvert = Expression.Convert(keyAccess, typeof(object));
                GetKeyValue = Expression.Lambda<Func<T, object>>(keyConvert, entityParam).Compile();

                var keyValueParam = Expression.Parameter(typeof(object), "value");
                var keyConvertBack = Expression.Convert(keyValueParam, KeyProperty.PropertyType);
                var keyAssign = Expression.Assign(keyAccess, keyConvertBack);
                SetKeyValue = Expression.Lambda<Action<T, object>>(keyAssign, entityParam, keyValueParam).Compile();


                // find concurrency property
                ConcurrencyProperty = type.GetProperties().FirstOrDefault(p => p.GetCustomAttributes(concurrencyAttributeType, true).Any());
                if (ConcurrencyProperty != null)
                {
                    var concurrencyAttr = ConcurrencyProperty.GetCustomAttributes(typeof(DBNameAttribute), true).FirstOrDefault() as DBNameAttribute;
                    ConcurrencyDBName = concurrencyAttr?.DBName ?? ConcurrencyProperty.Name;
                    HasConcurrency = true;
                    var concurrencyAccess = Expression.Property(entityParam, ConcurrencyProperty);
                    var concurrencyConvert = Expression.Convert(concurrencyAccess, typeof(object));
                    GetConcurrencyValue = Expression.Lambda<Func<T, object>>(concurrencyConvert, entityParam).Compile();

                    var concurrencyValueParam = Expression.Parameter(typeof(object), "value");
                    var concurrencyConvertBack = Expression.Convert(concurrencyValueParam, ConcurrencyProperty.PropertyType);
                    var concurrencyAssign = Expression.Assign(concurrencyAccess, concurrencyConvertBack);
                    SetConcurrencyValue = Expression.Lambda<Action<T, object>>(concurrencyAssign, entityParam, concurrencyValueParam).Compile();
                }
                else
                {
                    HasConcurrency = false;
                    ConcurrencyDBName = null;
                    GetConcurrencyValue = null;
                    SetConcurrencyValue = null;
                }

                //AllPropertyDelegates = new Dictionary<string, Func<T, object>>();
                //foreach (var prop in type.GetProperties().Where(IsTrackableProperty))
                //{
                //    var propAccess = System.Linq.Expressions.Expression.Property(entityParam, prop);
                //    var propConvert = System.Linq.Expressions.Expression.Convert(propAccess, typeof(object));
                //    var propDelegate = System.Linq.Expressions.Expression.Lambda<Func<T, object>>(propConvert, entityParam).Compile();
                //    AllPropertyDelegates.Add(prop.Name, propDelegate);
                //}

                BuildPropertyDelegates();

                if (typeof(NotifierObject).IsAssignableFrom(typeof(T)))
                {
                    NotifierObject.PropertyUpdated += NObjPropertyUpdated;
                }

                StaticSaveCommand = new AsyncStaticDbCommand<T>(Save, CanSave); //, (ex) => TrackedEntityError?.Invoke(this, ex));


                istrackable = true;
            }
        }
        private static void WeakPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            // Try to get the Tracked<T> instance from the sender
            if (sender is T entity)
            {
                // You need a way to get the Tracked<T> instance from the entity.
                // For this, you can use a ConditionalWeakTable<T, Tracked<T>>.
                if (_entityToTracked.TryGetValue(entity, out var tracked))
                {
                    tracked.OnINotyifyPropertyChanged(entity, e);
                }
            }
        }
        private static void NObjPropertyUpdated(object sender, PropertyChangedWithValuesEventArgs e)
        {
            if (sender is T entity)
            {
                if (_entityToTracked.TryGetValue(entity, out var tracked))
                {
                    tracked.OnNotifierObjectPropertyUpdated(entity, e);
                }
            }
        }

        public static string UpdateCommand { get; set; }

        // ---------- Instance-level ----------
        private TrackedEntity()
        {
            SaveCommand = new AsyncDbCommand(Save, CanSave); //,(ex) => TrackedEntityError?.Invoke(this, ex));
        }
        public TrackedEntity(T entity) : this()
        {
            if (!IsTrackable)
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not trackable. Ensure Initialize() has been called and that the type has both a Key and Concurrency property defined.");
            KeyValue = GetKeyValue.Invoke(entity);
            if (KeyValue == null)
                throw new InvalidOperationException($"The entity of type {typeof(T).FullName} has a null key value.");

            Initializing = true;
            _entityRef = new WeakReference<T>(entity);
            referencevalid = true;
            ConcurrencyValue = GetConcurrencyValue?.Invoke(entity);
            EndInitialization();

        }
        /// <summary>
        /// Use this method to create a Tracked object in the Initializing state, before the entity is fully constructed.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="concurrency"></param>
        /// <param name="entity"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public TrackedEntity(object key, object concurrency, T entity) : this()
        {
            if (!IsTrackable)
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not trackable. Ensure Initialize() has been called and that the type has at least a Key property and ideally, a Concurrency property defined.");
            if (key == null)
                throw new InvalidOperationException($"The entity of type {typeof(T).FullName} has a null key value.");

            _entityRef = new WeakReference<T>(entity);
            referencevalid = true;
            Initializing = true;
            KeyValue = key;
            ConcurrencyValue = concurrency;
        }
        public void BeginInitialization(object key, object concurrency, out T entity)
        {
            if (key == null)
                throw new InvalidOperationException($"The entity of type {typeof(T).FullName} has a null key value.");

            entity = new T();

            _entityRef = new WeakReference<T>(entity);
            referencevalid = true;
            Initializing = true;
            SetKeyValue(entity, key);
            SetConcurrencyValue?.Invoke(entity, concurrency);
        }
        public void EndInitialization()
        {
            if (!Initializing)
                throw new InvalidOperationException("EndInitialization can only be called on an object that is in the initializing state");

            if (_entityRef.TryGetTarget(out var entity))
            {
                _originalValues.Clear();
                if (entity is NotifierObject nobj)
                {
                    _dirtyProps.Clear();
                    if (_entityToTracked.TryGetValue(entity, out _))
                        _entityToTracked.Remove(entity);
                    _entityToTracked.Add(entity, this);
                    _isDirtyCached = false;
                    DirtyCheckMode = DirtyCheckMode.Advanced;
                    nobj.Initializing = false;
                }
                else
                {
                    if (entity is INotifyPropertyChanged inpc)
                    {
                        _dirtyProps.Clear();

                        if (_entityToTracked.TryGetValue(entity, out _))
                            _entityToTracked.Remove(entity);

                        _entityToTracked.Add(entity, this);
                        inpc.PropertyChanged -= WeakPropertyChangedHandler;
                        inpc.PropertyChanged += WeakPropertyChangedHandler;
                        _isDirtyCached = false;
                        DirtyCheckMode = DirtyCheckMode.Cached;
                    }
                    else
                    {
                        DirtyCheckMode = DirtyCheckMode.FullScan;
                        _isDirtyCached = null; // unknown
                    }

                    // save original values for actual entity data columns - key is already not in AllPropertyDelegates so this should
                    // basically be anything that doesn't have an attribute which should be an actual data column
                    foreach (var kw in _allpropertydelegates.Where(x => !x.Value.Concurrency && !x.Value.Ignore && !x.Value.Optional && !x.Value.DBLoaded))
                        _originalValues.Add(kw.Key, kw.Value.Getter.Invoke(entity));
                }

                Initializing = false;
            }
            else
            {
                referencevalid = false;
                TrackedStateChanged?.Invoke(this, TrackedState.Invalid);
                (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
                throw new InvalidOperationException("The entity reference is no longer valid.");
            }
        }
        private void OnINotyifyPropertyChanged(T entity, PropertyChangedEventArgs e)
        {
            // If property is trackable, compare to original and update cache.
            if (!Initializing && _originalValues.TryGetValue(e.PropertyName, out var original))
            {
                bool dirtypre = _isDirtyCached.Value;

                if (_allpropertydelegates.TryGetValue(e.PropertyName, out var pd) && !pd.Optional && !pd.Ignore && !pd.Concurrency && !pd.DBLoaded)
                {
                    var current = pd.Getter.Invoke(entity);
                    if (!Foundation.ValueEquals(current, original))
                    {
                        _dirtyProps.Add(e.PropertyName);
                        _isDirtyCached = true;
                    }
                    else
                    {
                        _dirtyProps.Remove(e.PropertyName);
                        _isDirtyCached = _dirtyProps.Count > 0;
                    }
                    if (dirtypre != _isDirtyCached.Value)
                    {
                        _dirtyVersion++; // membership changed
                        DirtySetChanged?.Invoke(this, new DirtySetChangedEventArgs(_dirtyVersion, _dirtyProps.Count));

                        TrackedStateChanged?.Invoke(this, _isDirtyCached.Value ? TrackedState.Modified : TrackedState.Unchanged);
                        (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
                    }
                }
            }
            //else
            //{
            //    throw new InvalidOperationException($"Property '{e.PropertyName}' has not been tracked");
            //}
        }
        private void OnNotifierObjectPropertyUpdated(T entity, PropertyChangedWithValuesEventArgs e)
        {
            // with Advanced DirtyCheckMode, we get old and new values so we don't have to
            // store all original values on initialization - we only have to store it if
            // the property changes.
            if (!Initializing)
            {
                bool dirtypre = _isDirtyCached.Value;

                if (_allpropertydelegates.TryGetValue(e.PropertyName, out var pd) && !pd.Optional && !pd.Ignore && !pd.Concurrency && !pd.DBLoaded)
                {
                    if (_originalValues.TryGetValue(e.PropertyName, out var original))
                    {
                        //var current = AllPropertyDelegates[e.PropertyName].Invoke(entity);
                        if (!Foundation.ValueEquals(e.NewValue, original, pd.PropertyType))
                        {
                            _dirtyProps.Add(e.PropertyName);
                            _isDirtyCached = true;
                        }
                        else
                        {
                            _dirtyProps.Remove(e.PropertyName);
                            _isDirtyCached = _dirtyProps.Count > 0;
                        }
                    }
                    else
                    {
                        _originalValues[e.PropertyName] = e.OldValue;
                        _dirtyProps.Add(e.PropertyName);
                        _isDirtyCached = true;
                    }
                    if (dirtypre != _isDirtyCached.Value)
                    {
                        _dirtyVersion++; // membership changed
                        DirtySetChanged?.Invoke(this, new DirtySetChangedEventArgs(_dirtyVersion, _dirtyProps.Count));

                        TrackedStateChanged?.Invoke(this, _isDirtyCached.Value ? TrackedState.Modified : TrackedState.Unchanged);
                        (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
                    }
                }
            }
        }
        private bool initializing = true;
        public bool Initializing 
        {
            get => initializing;
            internal set 
            {
                if (initializing != value)
                {
                    initializing = value;
                    TrackedStateChanged?.Invoke(this, State);
                    (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
                }
            }
        }
        private WeakReference<T> _entityRef;
        private bool referencevalid = false;
        private readonly Dictionary<string, object> _originalValues = new Dictionary<string, object>();
        private readonly HashSet<string> _dirtyProps = new HashSet<string>();
        private int _dirtyVersion = 0;
        public int DirtyVersion => _dirtyVersion;
        public int DirtyCount => _dirtyProps.Count;
        public event EventHandler<DirtySetChangedEventArgs> DirtySetChanged;
        private bool? _isDirtyCached = null;
        public DirtyCheckMode DirtyCheckMode { get; private set; }
        public object KeyValue { get; }
        public object ConcurrencyValue { get; }
        public TrackedState State
        {
            get
            {
                if (Initializing) 
                    return TrackedState.Initializing;
                if (_entityRef.TryGetTarget(out var entity))
                {
                    if (_isDirtyCached.HasValue)
                    {
                        return _isDirtyCached.Value ? TrackedState.Modified : TrackedState.Unchanged;
                    }
                    foreach (var kv in _originalValues)
                    {
                        var current = _allpropertydelegates[kv.Key].Getter.Invoke(entity);
                        if (!Foundation.ValueEquals(current, kv.Value))
                        {
                            return TrackedState.Modified;
                        }
                    }
                    return TrackedState.Unchanged;
                }
                if (referencevalid)
                {
                    referencevalid = false;
                    TrackedStateChanged?.Invoke(this, TrackedState.Invalid);
                    (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
                }
                return TrackedState.Invalid;
            }
        }
        public IReadOnlyDictionary<string, (object OldValue, object NewValue)> DirtyProperties => GetDirtyProperties();
        private Dictionary<string, (object, object)> GetDirtyProperties()
        {
            var result = new Dictionary<string, (object, object)>();

            if (_entityRef.TryGetTarget(out var entity))
            {
                if (_isDirtyCached.HasValue)
                {
                    if (_isDirtyCached.Value)
                    {
                        foreach (var propName in _dirtyProps)
                        {
                            var original = _originalValues[propName];
                            var current = _allpropertydelegates[propName].Getter.Invoke(entity);
                            result[propName] = (original, current);
                        }
                    }
                }
                else
                {
                    foreach (var kv in _originalValues)
                    {
                        var current = _allpropertydelegates[kv.Key].Getter.Invoke(entity);
                        if (!Foundation.ValueEquals(current, kv.Value))
                        {
                            result[kv.Key] = (kv.Value, current);
                        }
                    }
                }
            }
            else if (referencevalid)
            {
                referencevalid = false;
                TrackedStateChanged?.Invoke(this, TrackedState.Invalid);
                (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
            }
            return result;
        }
        private static void BuildPropertyDelegates()
        {
            _allpropertydelegates = new Dictionary<string, PropertyDelegateInfo<T>>();
            var type = typeof(T);
            foreach (var prop in type.GetProperties().Where(x => x.CanWrite && x.CanRead))
            {
                var delegateinfo = new PropertyDelegateInfo<T>();
                var skip = false;
                foreach (var attr in prop.GetCustomAttributes())
                {
                    if (attr is ListKeyAttribute) skip = true;
                    if (attr is ListConcurrencyAttribute) delegateinfo.Concurrency = true;
                    if (attr is DBOptionalAttribute) delegateinfo.Optional = true;
                    if (attr is DBIgnoreAttribute) delegateinfo.Ignore = true;
                    if (attr is DisableDirtyAwareCopyAttribute) delegateinfo.DirtyAwareEnabled = false;
                    if (attr is DBLoadedTimeAttribute) delegateinfo.DBLoaded = true;
                }
                if (!skip)
                {
                    var proptype = prop.PropertyType;
                    if (!proptype.IsValueType && proptype != typeof(string) && !proptype.IsArray)
                        delegateinfo.Optional = true;

                    delegateinfo.PropertyType = proptype;

                    // parameter: (T entity)
                    var entityParam = Expression.Parameter(typeof(T), "entity");

                    // ===== Build Getter =====
                    var propAccess = Expression.Property(entityParam, prop);
                    var propConvert = Expression.Convert(propAccess, typeof(object));
                    var getter = Expression.Lambda<Func<T, object>>(propConvert, entityParam).Compile();

                    // ===== Build Setter =====
                    // parameters: (T entity, object value)
                    var valueParam = Expression.Parameter(typeof(object), "value");

                    var assign = Expression.Assign(
                        propAccess,
                        Expression.Convert(valueParam, prop.PropertyType));

                    var setter = Expression.Lambda<Action<T, object>>(assign, entityParam, valueParam).Compile();


                    var setMethod = prop.GetSetMethod(true);
                    delegateinfo.PublicSetter = setMethod.IsPublic;

                    delegateinfo.Getter = getter;
                    delegateinfo.Setter = setter;
                    _allpropertydelegates.Add(prop.Name, delegateinfo);
                }
            }
        }
        public bool TryGetEntity(out T e)
        {
            if (_entityRef.TryGetTarget(out var entity))
            {
                e = entity;
                return true;
            }
            if (referencevalid)
            {
                referencevalid = false;
                TrackedStateChanged?.Invoke(this, TrackedState.Invalid);
                (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
            }
            e = null;
            return false;
        }
        public bool CopyValues(T from, bool dirtyaware = false, bool optionalonly = false)
        {
            if (TryGetEntity(out T to))
            {
                Initializing = true;

                //by the time we're done - base state for the set will be false and we'll set if we find a dirty property
                var dirtyset = false;

                //method will be considered successful if it does not have to overwrite a dirty value - it will overwrite a 
                //dirty value, after all, what is in the database is now true, so the app will lose any changes it made
                //but if a value is dirty and the original value is what is being loaded
                bool success = true;

                var fromkey = GetKeyValue(from);
                var tokey = GetKeyValue(to);
                if (!Foundation.ValueEquals(fromkey, tokey)) throw new Exception($"Cannot copy values on {typeof(T).Name} with key {fromkey} to an object with key {tokey}");

                var mismatchrecords = new List<ConcurrencyMismatchRecord>();

                List<KeyValuePair<string, PropertyDelegateInfo<T>>> list = null;
                if (optionalonly)
                    list = _allpropertydelegates.Where(x => x.Value.Optional || x.Value.DBLoaded).ToList(); // || x.Value.Ignore).ToList();
                else
                    list = _allpropertydelegates.Where(x => !x.Value.Ignore).ToList(); 
                
                // right now I'm thinking tracker method should ignore DBIgnore properties - incoming loading objects will never have values in them anyway because OFR ignores them
                // if the app has done something with them, then the existing object should have whatever values they should have and that's always what we're copying into, so right
                // now I can't even think of a scenario where we would want to copy ignored stuff in - quite probably, they shouldn't even be in AllPropertyDelegates at all

                foreach (var delegateinfo in list)
                {
                    var fromval = delegateinfo.Value.Getter(from);
                    var toval = delegateinfo.Value.Getter(to);
                    var allowdirty = delegateinfo.Value.DirtyAwareEnabled;
                    var currentorigpresent = _originalValues.TryGetValue(delegateinfo.Key, out var currentorigval);
                    var dirtypresent = DirtyProperties.TryGetValue(delegateinfo.Key, out var dirty);

                    if (delegateinfo.Value.Optional) // || delegateinfo.Value.Ignore)
                    {
                        //for optional or Ignored properties, only copy the value
                        //if the one in the from object looks more interesting than the one in the to object

                        if (!Foundation.ValueEquals(fromval, toval) && (Foundation.IsDefaultOrNull(toval) || !Foundation.IsDefaultOrNull(fromval)))
                            delegateinfo.Value.Setter(to, fromval);
                    }
                    else
                    {
                        if (Foundation.ValueEquals(fromval, toval))
                        {
                            // just clean
                            if (currentorigpresent && !Foundation.ValueEquals(currentorigval, fromval))
                                _originalValues[delegateinfo.Key] = fromval;
                            _dirtyProps.Remove(delegateinfo.Key);
                        }
                        else if (delegateinfo.Value.Concurrency || !currentorigpresent || !dirtypresent || delegateinfo.Value.DBLoaded)
                        {
                            //clean and overwrite
                            //notifications should go out, but the property should stay clean
                            if (currentorigpresent && !Foundation.ValueEquals(currentorigval, fromval))
                                _originalValues[delegateinfo.Key] = fromval;
                            _dirtyProps.Remove(delegateinfo.Key);
                            delegateinfo.Value.Setter(to, fromval);

                        }
                        // at this point the property is dirty - if the incoming value is the same as
                        // the original value, then let the property stay dirty / preserve the user's pending update
                        else if (Foundation.ValueEquals(currentorigval, fromval) && allowdirty)
                        {
                            dirtyset = true;
                        }
                        // at this point there is a true, column level concurrency conflict - the user has dirtied the property with a new
                        // value but there is also a different new value coming in from the database - the database must win and the user
                        // must be warned
                        else
                        {
                            success = false;
                            mismatchrecords.Add(new ConcurrencyMismatchRecord { PropertyName = delegateinfo.Key, AppValue = toval, DBValue = fromval });
                            if (currentorigpresent && !Foundation.ValueEquals(currentorigval, fromval))
                                _originalValues[delegateinfo.Key] = fromval;
                            _dirtyProps.Remove(delegateinfo.Key);
                            delegateinfo.Value.Setter(to, fromval);
                        }
                    }
                }
                if (_isDirtyCached.HasValue && _isDirtyCached != dirtyset)
                {
                    _isDirtyCached = dirtyset;
                    TrackedStateChanged?.Invoke(this, _isDirtyCached.Value ? TrackedState.Modified : TrackedState.Unchanged);
                    (SaveCommand as AsyncDbCommand)?.RaiseCanExecuteChanged();
                }
                Initializing = false;
                if (!success) throw new DBEngineConcurrencyMismatchException($"Concurrency Mismatch on an object of type {typeof(T).Name} with key value {tokey}", tokey, mismatchrecords);
                return success;
            }
            else
            {
                throw new Exception("TryDirtyAwareCopy called on an tracker with no valid entity");
            }
        }


        public bool CanNotify => _isDirtyCached != null;
        public event EventHandler<TrackedState> TrackedStateChanged;
        //public static event EventHandler<Exception> TrackedEntityError;

        public ICommand SaveCommand { get; private set; }
        public static ICommand StaticSaveCommand { get; private set;}
        private async Task<bool> Save(DBEngine db)
        {
            if (TryGetEntity(out var entity))
            {
                return await Save(db, entity);
            }
            return false;
        }
        private static async Task<bool> Save(DBEngine db, T entity)
        {
            return await db.RunSqlUpdateAsync(entity, UpdateCommand, DBEngine.IsProcedure(UpdateCommand), CancellationToken.None, -1, null, DBEngine.Default.AutoParam(entity, UpdateCommand)).ConfigureAwait(false);
        }
        private bool CanSave() => !string.IsNullOrWhiteSpace(UpdateCommand) && State == TrackedState.Modified;
        private static bool CanSave(T entity) => entity != null;
        public override string ToString()
        {
            return $"{typeof(T).Name} [Key={KeyValue?.ToString() ?? "null"}, State={State}]";
        }
    }
    public enum TrackedState { Initializing, Unchanged, Modified, Invalid }
    public enum DirtyCheckMode { FullScan, Cached, Advanced }
    public class PropertyDelegateInfo<T>
    {
        public bool Optional { get; set; } = false;
        public bool Ignore { get; set; } = false;
        public bool Concurrency { get; set; } = false;
        public bool DBLoaded { get; set; } = false;
        public bool DirtyAwareEnabled { get; set; } = true;
        public bool PublicSetter { get; set; }
        public Type PropertyType { get; set; }
        public Func<T, object> Getter { get; set; }
        public Action<T, object> Setter { get; set; }
        public PropertyDelegateInfo<T> GetSanitized()
        {
            return new PropertyDelegateInfo<T>
            {
                Optional = Optional,
                Ignore = Ignore,
                Concurrency = Concurrency,
                DirtyAwareEnabled = DirtyAwareEnabled,
                PublicSetter = PublicSetter,
                PropertyType = PropertyType,
                Getter = Getter,
                Setter = null
            };
        }
    }
    public sealed class DirtySetChangedEventArgs : EventArgs
    {
        public int Version { get; }
        public int Count { get; }
        public DirtySetChangedEventArgs(int version, int count) { Version = version; Count = count; }
    }
}
