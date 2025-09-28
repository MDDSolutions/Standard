using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;


namespace MDDDataAccess
{
    public class Tracked<T> where T : class, new()
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
        private static Dictionary<string,PropertyDelegateInfo<T>> AllPropertyDelegates;
        private static readonly ConditionalWeakTable<T, Tracked<T>> _entityToTracked = new ConditionalWeakTable<T, Tracked<T>>();
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
                    tracked.OnEntityPropertyChanged(entity, e);
                }
            }
        }

        private static void NObjPropertyUpdated(object sender, PropertyChangedWithValuesEventArgs e)
        {
            if (sender is T entity)
            {
                if (_entityToTracked.TryGetValue(entity, out var tracked))
                {
                    tracked.OnPropertyUpdated(entity, e);
                }
            }
        }

        // ---------- Instance-level ----------
        public Tracked(T entity)
        {
            if (!IsTrackable)
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not trackable. Ensure Initialize() has been called and that the type has both a Key and Concurrency property defined.");
            KeyValue = GetKeyValue.Invoke(entity);
            if (KeyValue == null)
                throw new InvalidOperationException($"The entity of type {typeof(T).FullName} has a null key value.");

            Initializing = true;
            _entityRef = new WeakReference<T>(entity);
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
        public Tracked(object key, object concurrency, T entity)
        {
            if (!IsTrackable)
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not trackable. Ensure Initialize() has been called and that the type has at least a Key property and ideally, a Concurrency property defined.");
            if (key == null)
                throw new InvalidOperationException($"The entity of type {typeof(T).FullName} has a null key value.");

            _entityRef = new WeakReference<T>(entity);

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

                    foreach (var kw in AllPropertyDelegates.Where(x => !x.Value.Concurrency))
                        _originalValues.Add(kw.Key, kw.Value.Getter.Invoke(entity));
                }

                Initializing = false;
            }
            else
            {
                throw new InvalidOperationException("The entity reference is no longer valid.");
            }
        }
        private void OnEntityPropertyChanged(T entity, PropertyChangedEventArgs e)
        {
            // If property is trackable, compare to original and update cache.
            if (!Initializing && _originalValues.TryGetValue(e.PropertyName, out var original))
            {
                var current = AllPropertyDelegates[e.PropertyName].Getter.Invoke(entity);
                if (!DBEngine.ValueEquals(current, original))
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
            //else
            //{
            //    throw new InvalidOperationException($"Property '{e.PropertyName}' has not been tracked");
            //}
        }
        private void OnPropertyUpdated(T entity, PropertyChangedWithValuesEventArgs e)
        {
            // with Advanced DirtyCheckMode, we get old and new values so we don't have to
            // store all original values on initialization - we only have to store it if
            // the property changes.
            if (!Initializing)
            {
                if (_originalValues.TryGetValue(e.PropertyName, out var original))
                {
                    //var current = AllPropertyDelegates[e.PropertyName].Invoke(entity);
                    if (!DBEngine.ValueEquals(e.NewValue, original))
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
            }
        }
        public bool Initializing { get; internal set; } = true;
        private WeakReference<T> _entityRef;
        private readonly Dictionary<string, object> _originalValues = new Dictionary<string, object>();
        private readonly HashSet<string> _dirtyProps = new HashSet<string>();
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
                        var current = AllPropertyDelegates[kv.Key].Getter.Invoke(entity);
                        if (!DBEngine.ValueEquals(current, kv.Value))
                        {
                            return TrackedState.Modified;
                        }
                    }
                    return TrackedState.Unchanged;
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
                            var current = AllPropertyDelegates[propName].Getter.Invoke(entity);
                            result[propName] = (original, current);
                        }
                    }
                }
                else
                {
                    foreach (var kv in _originalValues)
                    {
                        var current = AllPropertyDelegates[kv.Key].Getter.Invoke(entity);
                        if (!DBEngine.ValueEquals(current, kv.Value))
                        {
                            result[kv.Key] = (kv.Value, current);
                        }
                    }
                }
            }
            return result;
        }
        private static void BuildPropertyDelegates()
        {
            AllPropertyDelegates = new Dictionary<string, PropertyDelegateInfo<T>>();
            var type = typeof(T);
            foreach (var prop in type.GetProperties().Where(x => x.CanWrite && x.CanRead))
            {
                var delegateinfo = new PropertyDelegateInfo<T>
                {
                    DirtyAwareEnabled = SupportsDirtyAwareCopy
                };
                var skip = false;
                foreach (var attr in prop.GetCustomAttributes())
                {
                    if (attr is ListKeyAttribute) skip = true;
                    if (attr is ListConcurrencyAttribute) delegateinfo.Concurrency = true;
                    if (attr is DBOptionalAttribute) delegateinfo.Optional = true;
                    if (attr is DBIgnoreAttribute) delegateinfo.Ignore = true;
                    if (attr is DisableDirtyAwareCopyAttribute) delegateinfo.DirtyAwareEnabled = false;
                }
                if (!skip)
                {
                    var proptype = prop.PropertyType;
                    if (!proptype.IsValueType && proptype != typeof(string) && !proptype.IsArray)
                        delegateinfo.Optional = true;


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

                    delegateinfo.Getter = getter;
                    delegateinfo.Setter = setter;
                    AllPropertyDelegates.Add(prop.Name, delegateinfo);
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
            e = null;
            return false;
        }
        //public void CopyValues(T from, bool withinitialization)
        //{
        //    if (TryGetEntity(out T to))
        //    {
        //        if (withinitialization) Initializing = true;

        //        var fromkey = GetKeyValue(from);
        //        var tokey = GetKeyValue(to);
        //        if (!DBEngine.ValueEquals(fromkey, tokey)) throw new Exception($"Cannot copy values on {typeof(T).Name} with key {fromkey} to an object with key {tokey}");

        //        foreach (var delegateinfo in AllPropertyDelegates.Values)
        //        {
        //            var copy = true;
        //            var fromval = delegateinfo.Getter(from);
        //            if (delegateinfo.Optional || delegateinfo.Ignore)
        //            {
        //                //for optional or Ignored properties, only copy the value
        //                //if the one in the from object looks more interesting than the one in the to object
        //                var toval = delegateinfo.Getter(to);

        //                copy = !DBEngine.ValueEquals(fromval, toval) && (DBEngine.IsDefaultOrNull(toval) || !DBEngine.IsDefaultOrNull(fromval));
        //            }
        //            if (copy) delegateinfo.Setter(to, fromval);
        //        }
        //        if (withinitialization) EndInitialization();
        //    }
        //    else
        //    {
        //        throw new Exception("Copy Values called on an tracker with no valid entity");
        //    }
        //}
        public bool CopyValues(T from, bool dirtyaware = false)
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
                if (!DBEngine.ValueEquals(fromkey, tokey)) throw new Exception($"Cannot copy values on {typeof(T).Name} with key {fromkey} to an object with key {tokey}");

                var mismatchrecords = new List<ConcurrencyMismatchRecord>();

                foreach (var delegateinfo in AllPropertyDelegates)
                {
                    var fromval = delegateinfo.Value.Getter(from);
                    var toval = delegateinfo.Value.Getter(to);
                    var allowdirty = delegateinfo.Value.DirtyAwareEnabled;
                    var currentorigpresent = _originalValues.TryGetValue(delegateinfo.Key, out var currentorigval);
                    var dirtypresent = DirtyProperties.TryGetValue(delegateinfo.Key, out var dirty);

                    if (delegateinfo.Value.Optional || delegateinfo.Value.Ignore)
                    {
                        //for optional or Ignored properties, only copy the value
                        //if the one in the from object looks more interesting than the one in the to object

                        if (!DBEngine.ValueEquals(fromval, toval) && (DBEngine.IsDefaultOrNull(toval) || !DBEngine.IsDefaultOrNull(fromval)))
                            delegateinfo.Value.Setter(to, fromval);
                    }
                    else
                    {

                        if (DBEngine.ValueEquals(fromval, toval))
                        {
                            // just clean
                            if (currentorigpresent && !DBEngine.ValueEquals(currentorigval, fromval))
                                _originalValues[delegateinfo.Key] = fromval;
                            _dirtyProps.Remove(delegateinfo.Key);
                        }
                        else if (ConcurrencyProperty.Name == delegateinfo.Key || !currentorigpresent || !dirtypresent)
                        {
                            //clean and overwrite
                            //notifications should go out, but the property should stay clean
                            if (currentorigpresent && !DBEngine.ValueEquals(currentorigval, fromval))
                                _originalValues[delegateinfo.Key] = fromval;
                            _dirtyProps.Remove(delegateinfo.Key);
                            delegateinfo.Value.Setter(to, fromval);

                        }
                        // at this point the property is dirty - if the incoming value is the same as
                        // the original value, then let the property stay dirty / preserve the user's pending update
                        else if (DBEngine.ValueEquals(currentorigval, fromval) && allowdirty)
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
                            if (currentorigpresent && !DBEngine.ValueEquals(currentorigval, fromval))
                                _originalValues[delegateinfo.Key] = fromval;
                            _dirtyProps.Remove(delegateinfo.Key);
                            delegateinfo.Value.Setter(to, fromval);
                        }
                    }
                }
                if (_isDirtyCached.HasValue) _isDirtyCached = dirtyset;
                Initializing = false;
                if (!success) throw new DBEngineConcurrencyMismatchException($"Concurrency Mismatch on an object of type {typeof(T).Name} with key value {tokey}", tokey, mismatchrecords);
                return success;
            }
            else
            {
                throw new Exception("TryDirtyAwareCopy called on an tracker with no valid entity");
            }
        }
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
        public bool DirtyAwareEnabled { get; set; } = false;
        public Func<T, object> Getter { get; set; }
        public Action<T, object> Setter { get; set; }
    }

}
