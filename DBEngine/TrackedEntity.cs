using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MDDDataAccess
{
    public abstract class TrackedEntity<TObj> : ITrackedEntity, INotifyPropertyChanged where TObj : TrackedEntity<TObj>
    {
        private Dictionary<string, object> _originalValues;
        private bool _isInitializing = true;
        private bool _isUpdating = false;
        private bool _updatedValues = false;
        public delegate void PropertyChangedEventHandler<T>(T sender, PropertyChangedWithValuesEventArgs e);
        public static event PropertyChangedEventHandler<TObj> EntityUpdated;
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<TProp>(ref TProp field, TProp value, [CallerMemberName] string propertyName = null)
        {
            if (typeof(TProp).IsArray)
            {
                var arr1 = field as Array;
                var arr2 = value as Array;
                if (ReferenceEquals(arr1, arr2) || (arr1 != null && arr2 != null && arr1.Length == arr2.Length && arr1.Cast<object>().SequenceEqual(arr2.Cast<object>())))
                    return false;
            }
            else if (EqualityComparer<TProp>.Default.Equals(field, value))
            {
                return false;
            }

            if (_isUpdating) _updatedValues = true;

            // Record original value if not already done.
            if (!_isInitializing && !_isUpdating)
            {
                if (_originalValues == null) _originalValues = new Dictionary<string, object>();

                if (!_originalValues.ContainsKey(propertyName))
                {   //only add an entry to original values if the entry is not already there
                    //since the value can be updated more than once (and of course only the first update would be from the "original" value)
                    _originalValues[propertyName] = field;
                }
                else if (_originalValues[propertyName] == null)
                {
                    if (value == null) _originalValues.Remove(propertyName);
                }
                else if (_originalValues[propertyName].Equals(value))
                {   // if value has been reverted back to the original value then remove the entry from original values
                    _originalValues.Remove(propertyName);
                }
            }

            TProp oldvalue = field;
            field = value;
            if (!_isInitializing)
            {
                OnEntityUpdated((TObj)this, propertyName, oldvalue, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
            return true;
        }
        protected TProp GetOriginalValue<TProp>(string propertyName)
        {
            if (_originalValues != null && _originalValues.TryGetValue(propertyName, out var value))
            {
                return (TProp)value;
            }

            return default(TProp);
        }
        public void BeginInit() => _isInitializing = true;
        public void EndInit()
        {
            LoadedAt = Environment.TickCount;
            ResetDirtyTracking();
            _isInitializing = false;
            OnEntityUpdated((TObj)this, null, null, null);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        //Use BeginUpdate/EndUpdate when you are going to be updating multiple properties at once and don't want to raise the PropertyChanged event for each one
        //it is basically for ObjectFromReader - if we're loading a record from the database that hasn't changed, we don't want any events raised
        public void BeginUpdate() => _isUpdating = true;
        public void EndUpdate()
        {
            LoadedAt = Environment.TickCount;
            ResetDirtyTracking();
            _isUpdating = false;
            if (_updatedValues)
            {
                _updatedValues = false;
                OnEntityUpdated((TObj)this, null, null, null);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }

        public int LoadedAt { get; private set; }

        public void UpdateLoadTime()
        {
            LoadedAt = Environment.TickCount;
        }
        public bool IsDirty => _originalValues != null && _originalValues.Any();

        public bool IsTracked(DBEngine db)
        {
            var t = db.GetOrCreateTracker<TObj>() as IObjectTracker;
            return t.Exists(this);
        }
        protected static void OnEntityUpdated(TObj sender, [CallerMemberName] string propertyName = null, object oldvalue = null, object newvalue = null)
        {
            EntityUpdated?.Invoke(sender, new PropertyChangedWithValuesEventArgs(propertyName,oldvalue, newvalue));
        }
        public void RaiseEntityUpdated(string propertyName, object oldValue, object newValue)
        {
            OnEntityUpdated((TObj)this, propertyName, oldValue, newValue);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));  
        }

        public IEnumerable<string> GetDirtyProperties()
        {
            return _originalValues?.Keys ?? Enumerable.Empty<string>();
        }
        public void ResetDirtyTracking()
        {
            _originalValues?.Clear();
        }

        //public  void EnsureCorrectPropertyUsage()
        //{
        //    if (!_hasValidated)
        //    {
        //        var type = this.GetType();
        //        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        //        {
        //            if (prop.Name == "created_date" || prop.Name == "modified_date" || Attribute.IsDefined(prop, typeof(ListKeyAttribute)))
        //                continue;

        //            var setMethod = prop.GetSetMethod();
        //            if (setMethod == null)
        //                continue;

        //            //This doesn't work...

        //            //var body = setMethod.GetMethodBody();
        //            //if (body == null)
        //            //    throw new InvalidOperationException($"The property '{prop.Name}' does not use 'SetProperty'.");

        //            //var il = body.GetILAsByteArray();
        //            //bool usesSetProperty = false;

        //            //Console.WriteLine($"Analyzing property {prop.Name}...");

        //            //for (int i = 0; i < il.Length; i++)
        //            //{
        //            //    // The call or callvirt opcode has values 0x28 and 0x6F, respectively.
        //            //    if (il[i] == 0x28 || il[i] == 0x6F)
        //            //    {
        //            //        var methodToCall = type.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
        //            //        if (methodToCall.Name == "SetProperty")
        //            //        {
        //            //            usesSetProperty = true;
        //            //            break;
        //            //        }
        //            //    }
        //            //}

        //            //if (!usesSetProperty)
        //            //    throw new InvalidOperationException($"The property '{prop.Name}' does not use 'SetProperty'.");
        //        }
        //        _hasValidated = true;
        //    }
        //}
        //private static bool _hasValidated = false;
    }
    public interface ITrackedEntity
    {
        void BeginInit();
        void EndInit();
        void BeginUpdate();
        void EndUpdate();
        void UpdateLoadTime();
        int LoadedAt { get; }
        void RaiseEntityUpdated(string propertyName, object oldValue, object newValue);
        IEnumerable<string> GetDirtyProperties();
        bool IsTracked (DBEngine db);
        bool IsDirty { get; }
        void ResetDirtyTracking();
        //this method isn't working - fix it another day
        //void EnsureCorrectPropertyUsage();
    }


    public class PropertyChangedWithValuesEventArgs : EventArgs
    {
        public string PropertyName { get; }
        public object OldValue { get; }
        public object NewValue { get; }

        public PropertyChangedWithValuesEventArgs(string propertyName, object oldValue, object newValue)
        {
            PropertyName = propertyName;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }


}