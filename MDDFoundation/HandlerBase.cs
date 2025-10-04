using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public class GenericHandler<T> : HandlerBase<T>
    {
        public Func<T, object, Task> HandleFunc = null;
        public async override Task HandleAsync(T inObj, object ParamObj = null)
        {
            if (HandleFunc == null) throw new Exception("you must set HandleFunc");
            await HandleFunc(inObj, ParamObj);
        }
    }
    public abstract class HandlerBase<T> : ILoader<T>
    {
        protected HandlerBase() => _instances.Add(this);
        public virtual void Unregister()
        {
            IsActive = false;
            _instances.Remove(this);
        }
        public virtual bool IsActive { get; set; } = true;
        public abstract Task HandleAsync(T inObj, object ParamObj = null);
        public Func<HandlerBase<T>, T, object, Task> HandleBackAsync { get; set; } = null;
        public virtual bool ValidFor(T inObj, object ParamObj = null) => IsActive;



        public virtual string HandlerType { get; set; }
        public virtual string HandlerCaption { get; set; }
        public virtual T Value { get; set; }
        public virtual object ReferencingObject { get; set; }



        private static readonly List<HandlerBase<T>> _instances = new List<HandlerBase<T>>();
        public static IReadOnlyList<HandlerBase<T>> Instances => _instances;




        // ILoader implementation - temporary - to be removed later
        public ILoader<T> LoadItem(T inObj, object ParamObj = null)
        {
            HandleAsync(inObj, ParamObj).GetAwaiter().GetResult();
            return this;
        }

        public async Task<ILoader<T>> LoadItemAsync(T inObj, object ParamObj = null)
        {
            await HandleAsync(inObj, ParamObj);
            return this;
        }
        public string LoaderType => HandlerType;
        public string LoaderCaption => HandlerCaption;
        public T LoadedObject => Value;
        public bool ValidAndActive => IsActive;
    }
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
