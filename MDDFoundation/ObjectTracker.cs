using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

//DEPRECATED

namespace MDDFoundation
{

    //public class TrackedObject<T>
    //{
    //    public T Object { get; }
    //    public DateTime LoadedAt { get; private set; }

    //    private readonly PropertyChangedEventHandler propertyChangedHandler;

    //    public TrackedObject(T obj, Action<T> onPropertyChangedAction)
    //    {
    //        Object = obj;
    //        LoadedAt = DateTime.Now;

    //        if (obj is INotifyPropertyChanged inpc)
    //        {
    //            propertyChangedHandler = (sender, args) => onPropertyChangedAction((T)sender);
    //            inpc.PropertyChanged += propertyChangedHandler;
    //        }
    //    }

    //    public void UpdateLoadTime()
    //    {
    //        LoadedAt = DateTime.Now;
    //    }

    //    public void UnsubscribeFromChanges()
    //    {
    //        if (Object is INotifyPropertyChanged inpc && propertyChangedHandler != null)
    //        {
    //            inpc.PropertyChanged -= propertyChangedHandler;
    //        }
    //    }
    //}


    //public class ObjectTracker<T, TKey>
    //{
    //    public delegate void ObjectLoadedHandler(T loadedObject);
    //    public event ObjectLoadedHandler OnObjectLoaded;

    //    private readonly ConcurrentDictionary<TKey, TrackedObject<T>> trackedObjects = new ConcurrentDictionary<TKey, TrackedObject<T>>();
    //    private readonly Func<T, TKey> keySelector;

    //    public ObjectTracker(Expression<Func<T, TKey>> keyPropertyExpression)
    //    {
    //        if (keyPropertyExpression.Body is MemberExpression memberExpression &&
    //            memberExpression.Member is PropertyInfo)
    //        {
    //            var property = (PropertyInfo)memberExpression.Member;
    //            keySelector = (Func<T, TKey>)property.GetGetMethod().CreateDelegate(typeof(Func<T, TKey>));
    //        }
    //        else
    //        {
    //            throw new ArgumentException("The expression must be a property selector", nameof(keyPropertyExpression));
    //        }
    //    }

    //    public void Load(T obj)
    //    {
    //        var key = keySelector(obj);

    //        trackedObjects.AddOrUpdate(key,
    //            k => new TrackedObject<T>(obj, RaiseObjectLoadedEvent),
    //            (k, existingObj) =>
    //            {
    //                existingObj.UpdateLoadTime();
    //                return existingObj;
    //            });

    //        RaiseObjectLoadedEvent(obj);
    //    }
    //    private void RaiseObjectLoadedEvent(T obj)
    //    {
    //        OnObjectLoaded?.Invoke(obj);
    //    }

    //    public DateTime? GetLoadTime(T obj)
    //    {
    //        var key = keySelector(obj);
    //        return GetLoadTime(key);
    //    }
    //    public DateTime? GetLoadTime(TKey key)
    //    {
    //        return trackedObjects.TryGetValue(key, out var trackedObject) ? trackedObject.LoadedAt : (DateTime?)null;
    //    }
    //    public T GetObject(TKey key)
    //    {
    //        return trackedObjects.TryGetValue(key, out var trackedObject) ? trackedObject.Object : default(T);
    //    }
    //}
}
