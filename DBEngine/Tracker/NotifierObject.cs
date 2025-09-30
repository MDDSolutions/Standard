using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;


namespace MDDDataAccess
{
    public class NotifierObject : INotifyPropertyChanged
    {
        public bool Initializing = true;
        protected bool SetProperty<TProp>(ref TProp field, TProp value, [CallerMemberName] string propertyName = null)
        {
            if (Initializing)
            {
                field = value;
                return true;
            }
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

            TProp oldvalue = field;
            field = value;

            OnPropertyUpdated(this, propertyName, oldvalue, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        protected static void OnPropertyUpdated(object sender, [CallerMemberName] string propertyName = null, object oldvalue = null, object newvalue = null)
        {
            PropertyUpdated?.Invoke(sender, new PropertyChangedWithValuesEventArgs(propertyName, oldvalue, newvalue));
        }
        internal static void RaisePropertyChanged(object sender, [CallerMemberName] string propertyName = null)
        {
            PropertyUpdated?.Invoke(sender, new PropertyChangedWithValuesEventArgs(propertyName, null, null));
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public static event EventHandler<PropertyChangedWithValuesEventArgs> PropertyUpdated;
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
