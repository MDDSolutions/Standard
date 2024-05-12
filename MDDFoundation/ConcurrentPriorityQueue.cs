using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public class ConcurrentPriorityQueue<T>
    {
        private ConcurrentDictionary<int, ConcurrentQueue<T>> dicqueue = new ConcurrentDictionary<int, ConcurrentQueue<T>>();
        private ConcurrentDictionary<int,T> removed = new ConcurrentDictionary<int,T>();

        public void Enqueue(T item, int priority)
        {
            var q = dicqueue.GetOrAdd(priority, (a) => new ConcurrentQueue<T>());
            if (removed.TryRemove(item.GetHashCode(), out T rem))
            {
                if (!q.Contains(item)) throw new InvalidOperationException("Cannot change priority");
            }
            else
            {
                q.Enqueue(item);
            }
        }
        public T Dequeue()
        {
            foreach (var key in dicqueue.Keys.OrderBy(x => x).ToList())
            {
                if (dicqueue.TryGetValue(key, out ConcurrentQueue<T> q))
                {
                    bool tryagain = true;
                    while (tryagain)
                    {
                        if (q.TryDequeue(out T item))
                        {
                            if (removed.TryRemove(item.GetHashCode(), out T r))
                            {
                                TryRemove(r);
                            }
                            else
                            {
                                return item;
                            }
                        }
                        else
                        {
                            tryagain = false;
                        }
                    }

                }
            }
            return default;
        }
        public bool TryDequeue(out T result)
        {
            result = Dequeue();
            return result != null;
        }
        public int Count
        {
            get
            {
                int count = 0;
                foreach (var key in dicqueue.Keys.OrderBy(x => x).ToList())
                {
                    count += dicqueue[key].Count;
                }
                return count - removed.Count;
            }
        }
        public override string ToString()
        {
            string str = "";
            int count = 0;
            foreach (var key in dicqueue.Keys.OrderBy(x => x).ToList())
            {
                str += $", {key}: {dicqueue[key].Count}";
                count += dicqueue[key].Count;
            }
            return $"Overall: {count}, (minus {removed.Count} removed)" + str;
        }
        public List<T> Values
        {
            get 
            { 
                var list = new List<T>();
                foreach (var key in dicqueue.Keys.OrderBy(x => x))
                {
                    if (dicqueue.TryGetValue(key, out ConcurrentQueue<T> q))
                    {
                        foreach (var item in q)
                        {
                            if (!removed.ContainsKey(item.GetHashCode())) list.Add(item); 
                        }
                    }
                }
                return list;
            }
        }
        public bool TryRemove(T item)
        {
            foreach (var key in dicqueue.Keys.OrderBy(x => x))
            {
                if (dicqueue.TryGetValue(key, out ConcurrentQueue<T> q))
                {
                    //bool success = false;

                    //foreach (var qitem in q.Where(x => x.Equals(item)))
                    //{
                    //    removed.GetOrAdd(item.GetHashCode(), item);
                    //    success = true;
                    //}
                    //return success;

                    if (q.Contains(item))
                    {
                        removed.GetOrAdd(item.GetHashCode(), item);
                        return true;
                    }
                }
            }
            return false;
        }
        public bool Any(Func<T,bool> predicate = null)
        {
            foreach (var key in dicqueue.Keys.OrderBy(x => x))
            {
                if (dicqueue.TryGetValue(key, out ConcurrentQueue<T> q))
                {
                    IEnumerable<T> list;
                    if (predicate != null)
                        list = q.Where(predicate);
                    else
                        list = q.ToList();
                    if (list.Select(x => x.GetHashCode()).Except(removed.Keys).Any()) return true;
                }
            }
            return false;
        }
    }
}
