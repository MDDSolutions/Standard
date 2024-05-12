using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public interface ILoader<T>
    {
        string LoaderType { get; }
        ILoader<T> LoadItem(T inObj, object ParamObj = null);
        Task<ILoader<T>> LoadItemAsync(T inObj, object ParamObj = null);
        string LoaderCaption { get; }
        T LoadedObject { get; }
        bool ValidAndActive { get; }
        object ReferencingObject { get; set; }
    }
}
