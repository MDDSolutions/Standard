using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;


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
        public static PropertyInfo ConcurrencyProperty { get; private set; }
        public static string ConcurrencyDBName { get; private set; }
        // Compiled delegates for speed
        public static Func<T, object> GetKeyValue;
        public static Action<T, object> SetKeyValue;
        public static Func<T, object> GetConcurrencyValue;
        public static Action<T, object> SetConcurrencyValue;
        private static Dictionary<string,Func<T, object>> AllPropertyDelegates;
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

                // find key property
                KeyProperty = type.GetProperties().FirstOrDefault(p => p.GetCustomAttributes(keyAttributeType, true).Any());
                if (KeyProperty == null)
                {
                    istrackable = false;
                    return;
                }
                var keyAttr = KeyProperty.GetCustomAttributes(typeof(DBNameAttribute), true).FirstOrDefault() as DBNameAttribute;
                KeyDBName = keyAttr?.DBName ?? KeyProperty.Name;

                var entityParam = System.Linq.Expressions.Expression.Parameter(typeof(T), "entity");
                var keyAccess = System.Linq.Expressions.Expression.Property(entityParam, KeyProperty);
                var keyConvert = System.Linq.Expressions.Expression.Convert(keyAccess, typeof(object));
                GetKeyValue = System.Linq.Expressions.Expression.Lambda<Func<T, object>>(keyConvert, entityParam).Compile();

                var keyValueParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "value");
                var keyConvertBack = System.Linq.Expressions.Expression.Convert(keyValueParam, KeyProperty.PropertyType);
                var keyAssign = System.Linq.Expressions.Expression.Assign(keyAccess, keyConvertBack);
                SetKeyValue = System.Linq.Expressions.Expression.Lambda<Action<T, object>>(keyAssign, entityParam, keyValueParam).Compile();


                // find concurrency property
                ConcurrencyProperty = type.GetProperties().FirstOrDefault(p => p.GetCustomAttributes(concurrencyAttributeType, true).Any());
                if (ConcurrencyProperty != null)
                {
                    var concurrencyAttr = ConcurrencyProperty.GetCustomAttributes(typeof(DBNameAttribute), true).FirstOrDefault() as DBNameAttribute;
                    ConcurrencyDBName = concurrencyAttr?.DBName ?? ConcurrencyProperty.Name;
                    HasConcurrency = true;
                    var concurrencyAccess = System.Linq.Expressions.Expression.Property(entityParam, ConcurrencyProperty);
                    var concurrencyConvert = System.Linq.Expressions.Expression.Convert(concurrencyAccess, typeof(object));
                    GetConcurrencyValue = System.Linq.Expressions.Expression.Lambda<Func<T, object>>(concurrencyConvert, entityParam).Compile();

                    var concurrencyValueParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "value");
                    var concurrencyConvertBack = System.Linq.Expressions.Expression.Convert(concurrencyValueParam, ConcurrencyProperty.PropertyType);
                    var concurrencyAssign = System.Linq.Expressions.Expression.Assign(concurrencyAccess, concurrencyConvertBack);
                    SetConcurrencyValue = System.Linq.Expressions.Expression.Lambda<Action<T, object>>(concurrencyAssign, entityParam, concurrencyValueParam).Compile();
                }
                else
                {
                    HasConcurrency = false;
                    ConcurrencyDBName = null;
                    GetConcurrencyValue = null;
                    SetConcurrencyValue = null;
                }

                AllPropertyDelegates = new Dictionary<string, Func<T, object>>();
                foreach (var prop in type.GetProperties().Where(IsTrackableProperty))
                {
                    var propAccess = System.Linq.Expressions.Expression.Property(entityParam, prop);
                    var propConvert = System.Linq.Expressions.Expression.Convert(propAccess, typeof(object));
                    var propDelegate = System.Linq.Expressions.Expression.Lambda<Func<T, object>>(propConvert, entityParam).Compile();
                    AllPropertyDelegates.Add(prop.Name, propDelegate);
                }

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
                        // Use a weak handler or be sure to unsubscribe on Detach/Dispose to avoid leaks.
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

                    foreach (var kw in AllPropertyDelegates)
                        _originalValues.Add(kw.Key, kw.Value.Invoke(entity));
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
            if (_originalValues.TryGetValue(e.PropertyName, out var original))
            {
                var current = AllPropertyDelegates[e.PropertyName].Invoke(entity);
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
                        var current = AllPropertyDelegates[kv.Key].Invoke(entity);
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
                            var current = AllPropertyDelegates[propName].Invoke(entity);
                            result[propName] = (original, current);
                        }
                    }
                }
                else
                {
                    foreach (var kv in _originalValues)
                    {
                        var current = AllPropertyDelegates[kv.Key].Invoke(entity);
                        if (!DBEngine.ValueEquals(current, kv.Value))
                        {
                            result[kv.Key] = (kv.Value, current);
                        }
                    }
                }
            }
            return result;
        }
        private static bool IsTrackableProperty(PropertyInfo prop)
        {
            if (!prop.CanRead || !prop.CanWrite)
                return false;

            if (prop.GetCustomAttributes(typeof(ListKeyAttribute), true).Any())
                return false;

            if (prop.GetCustomAttributes(typeof(ListConcurrencyAttribute), true).Any())
                return false;

            if (prop.GetCustomAttributes(typeof(DBIgnoreAttribute), true).Any())
                return false;

            if (prop.GetCustomAttributes(typeof(DBOptionalAttribute), true).Any())
                return false;

            var type = prop.PropertyType;

            // Track value types (primitives, structs, enums, etc.)
            if (type.IsValueType)
                return true;            

            // Always track strings
            if (type == typeof(string))
                return true;

            // track arrays
            if (type.IsArray)
                return true;

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                return false;

            // Otherwise, skip (likely navigation property or complex type)
            return false;
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
        public override string ToString()
        {
            return $"{typeof(T).Name} [Key={KeyValue?.ToString() ?? "null"}, State={State}]";
        }
    }
    public enum TrackedState { Initializing, Unchanged, Modified, Invalid }
    public enum DirtyCheckMode { FullScan, Cached, Advanced }

    }
