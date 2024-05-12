using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public abstract class DBEntity
    {
        public virtual void SyncTo(DBEntity source)
        {
            foreach (var item in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.IsEnum || item.PropertyType.Equals(typeof(System.String))))
                    item.SetValue(this, item.GetValue(source, null), null);
        }
        public virtual Task Save(CancellationToken CancellationToken)
        {
            throw new NotImplementedException($"Save not implemented on {GetType().Name}");
        }
        public virtual Task Delete()
        {
            throw new NotImplementedException($"Delete not implemented on {GetType().Name}");
        }
    }
}
