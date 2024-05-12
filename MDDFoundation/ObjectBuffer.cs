using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
}
