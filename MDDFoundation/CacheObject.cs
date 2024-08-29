using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MDDFoundation
{
    public abstract class CacheObject<T> where T : CacheObject<T>
    {
        protected DateTime lastaccessed { get; private set; } = DateTime.Now;
        protected abstract bool IDMatch(T equalto);
        protected abstract bool IsStub { get; }
        protected virtual void Invalidate() { }



        protected static int hitcount { get; private set; } = 0;
        protected static int loadcount { get; private set; } = 0;
        protected static int prunecount { get; private set; } = 0;
        protected static List<T> cache { get; private set; } = new List<T>();
        public static int DefaultPruneMinutes { get; set; } = 30;
        protected static IList<T> ProcessResults(IList<T> qresult, int? pruneminutes = null)
        {
            var l = new List<T>();
            foreach (var qritem in qresult)
            {
                var citem = cache.Where(x => qritem.IDMatch(x)).FirstOrDefault();
                if (!qritem.IsStub)
                {
                    loadcount++;
                    l.Add(qritem);
                    if (citem != null) cache.Remove(citem);
                    cache.Add(qritem);
                }
                else
                {
                    if (citem != null)
                    {
                        citem.lastaccessed = DateTime.Now;
                        hitcount++;
                        l.Add(citem);
                    }
                    else
                        throw new Exception($"Object '{qritem}' was expected to be in cache");
                }
            }
            if (pruneminutes == null)
                PruneCache(DefaultPruneMinutes);
            else if (pruneminutes > 0)
                PruneCache(pruneminutes ?? 0);
            return l;
        }
        public static void PruneCache(int olderthanminutes)
        {
            var priorto = DateTime.Now.AddMinutes(-olderthanminutes);
            cache.Where(x => x.lastaccessed <= priorto).ToList().ForEach(x => x.Invalidate());
            prunecount += cache.RemoveAll(x => x.lastaccessed <= priorto);
            var t = typeof(T);
            Foundation.Log($"Cache of {t.Name}: size: {cache.Count}, hits: {hitcount}, loads: {loadcount}, pruned: {prunecount}");
        }
    }
}
