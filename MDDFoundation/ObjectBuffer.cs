using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MDDFoundation
{
    public class ObjectBuffer<T> where T : new()
    {
        private List<T> buffer = new List<T>();
        private int curpos;
        public T GetObject()
        {
            T t;
            if (buffer.Count > curpos)
                t = buffer[curpos];
            else
            {
                t = new T();
                buffer.Add(t);
            }
            curpos++;
            return t;
        }
        public void Reset()
        {
            curpos = 0;
        }
    }
    public class CategorizedObjectBuffer<T, TCategory>
    {
        private readonly ConcurrentDictionary<TCategory, ConcurrentBag<T>> buffer = new ConcurrentDictionary<TCategory, ConcurrentBag<T>>();
        private readonly ConcurrentDictionary<T, TCategory> inUse = new ConcurrentDictionary<T, TCategory>();
        private readonly Func<TCategory, T> create;

        public CategorizedObjectBuffer(Func<TCategory, T> newObject)
        {
            create = newObject;
        }

        public T GetObject(TCategory category)
        {
            if (buffer.TryGetValue(category, out var objects) && objects.TryTake(out var obj))
            {
                inUse[obj] = category;
                return obj;
            }
            else
            {
                var newObj = create(category);
                inUse[newObj] = category;
                return newObj;
            }
        }

        public void ReleaseObject(T obj)
        {
            if (!TryReleaseObject(obj))
                throw new InvalidOperationException("The object being released was not obtained from the buffer.");
        }

        public bool TryReleaseObject(T obj)
        {
            if (inUse.TryRemove(obj, out var category))
            {
                var objects = buffer.GetOrAdd(category, _ => new ConcurrentBag<T>());
                objects.Add(obj);
                return true;
            }
            else
            {
                return false;
            }
        }
    }
    public class PooledObjectWrapper<T, TCategory> : IDisposable where T : class
    {
        public static Func<TCategory, T> CreateInstance { get; set; }
        private static readonly CategorizedObjectBuffer<T, TCategory> Buffer;
        private readonly T _object;
        private bool _disposed;

        static PooledObjectWrapper()
        {
            Buffer = new CategorizedObjectBuffer<T, TCategory>(category => CreateInstance(category));
        }

        public PooledObjectWrapper(TCategory category)
        {
            _object = Buffer.GetObject(category);
        }

        public T Object => _object;

        public void Dispose()
        {
            if (!_disposed)
            {
                Buffer.ReleaseObject(_object);
                _disposed = true;
            }
        }
    }


}
