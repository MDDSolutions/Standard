using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace MDDFoundation
{
    /// <summary>
    /// Provides a generic collection that supports data binding and additionally supports sorting.
    /// See http://msdn.microsoft.com/en-us/library/ms993236.aspx
    /// If the elements are IComparable it uses that; otherwise compares the ToString()
    /// </summary>
    /// <typeparam name="T">The type of elements in the list.</typeparam>
    public class SortableBindingList<T> : BindingList<T>, IBindingListView where T : class
    {
        private bool _isSorted;
        private ListSortDirection _sortDirection = ListSortDirection.Ascending;
        private PropertyDescriptor _sortProperty;

        /// <summary>
        /// Initializes a new instance of the <see cref="SortableBindingList{T}"/> class.
        /// </summary>
        public SortableBindingList()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SortableBindingList{T}"/> class.
        /// </summary>
        /// <param name="list">An <see cref="T:System.Collections.Generic.IList`1" /> of items to be contained in the <see cref="T:System.ComponentModel.BindingList`1" />.</param>
        public SortableBindingList(IList<T> list)
            : base(list)
        {
        }
        /// <summary>
        /// Gets a value indicating whether the list supports sorting.
        /// </summary>
        protected override bool SupportsSortingCore
        {
            get { return true; }
        }
        /// <summary>
        /// Gets a value indicating whether the list is sorted.
        /// </summary>
        protected override bool IsSortedCore
        {
            get { return _isSorted; }
        }
        /// <summary>
        /// Gets the direction the list is sorted.
        /// </summary>
        protected override ListSortDirection SortDirectionCore
        {
            get { return _sortDirection; }
        }
        /// <summary>
        /// Gets the property descriptor that is used for sorting the list if sorting is implemented in a derived class; otherwise, returns null
        /// </summary>
        protected override PropertyDescriptor SortPropertyCore
        {
            get { return _sortProperty; }
        }

        /// <summary>
        /// Removes any sort applied with ApplySortCore if sorting is implemented
        /// </summary>
        protected override void RemoveSortCore()
        {
            _sortDirection = ListSortDirection.Ascending;
            _sortProperty = null;
            _isSorted = false; //thanks Luca
        }
        /// <summary>
        /// Sorts the items if overridden in a derived class
        /// </summary>
        /// <param name="prop"></param>
        /// <param name="direction"></param>
        protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection direction)
        {
            _sortProperty = prop;
            _sortDirection = direction;

            List<T> list = Items as List<T>;
            if (list == null) return;

            list.Sort(Compare);

            _isSorted = true;
            //fire an event that the list has been changed.
            OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
        }
        private int Compare(T lhs, T rhs)
        {
            var result = OnComparison(lhs, rhs);
            //invert if descending
            if (_sortDirection == ListSortDirection.Descending)
                result = -result;
            return result;
        }
        private int OnComparison(T lhs, T rhs)
        {
            object lhsValue = lhs == null ? null : _sortProperty.GetValue(lhs);
            object rhsValue = rhs == null ? null : _sortProperty.GetValue(rhs);
            if (lhsValue == null)
            {
                return (rhsValue == null) ? 0 : -1; //nulls are equal
            }
            if (rhsValue == null)
            {
                return 1; //first has value, second doesn't
            }
            if (lhsValue is IComparable)
            {
                return ((IComparable)lhsValue).CompareTo(rhsValue);
            }
            if (lhsValue.Equals(rhsValue))
            {
                return 0; //both are the same
            }
            //not comparable, compare ToString
            return lhsValue.ToString().CompareTo(rhsValue.ToString());
        }

        public delegate void ItemRemovedHandler(T ItemRemoved);
        public event ItemRemovedHandler ItemRemoved;
        protected override void RemoveItem(int index)
        {
            if (ItemRemoved != null)
            {
                T itemremoved = base[index];
                ItemRemoved(itemremoved);
            }
            base.RemoveItem(index);
        }

        public delegate void ItemInsertedHandler(T ItemInserted);
        public event ItemInsertedHandler ItemInserted;
        protected override void InsertItem(int index, T item)
        {
            base.InsertItem(index, item);
            if (ItemInserted != null)
                ItemInserted(item);
        }



        private List<T> originalitems = null;
        public bool SupportsFiltering => true;
        private string filter = null;
        private PropertyInfo[] filterproperties = null;
        public string Filter
        {
            get { return filter; }
            set
            {
                bool skip = false;
                if (value.Contains('"'))
                {   //if value has a double quote then it has to have an even number of double quotes
                    if ((value.Split('"').Length - 1) % 2 != 0)
                    {
                        skip = true;
                    }
                }

                if (!skip)
                {
                    filter = value;
                    if (string.IsNullOrWhiteSpace(filter))
                    {
                        filter = null;
                        RemoveFilter();
                    }
                    else
                    {
                        if (originalitems == null)
                        {
                            originalitems = (Items as List<T>).ToList();
                        }
                        if (filterproperties == null)
                        {
                            filterproperties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                        }

                        string[] filters;
                        if (filter.Contains("\""))
                        {
                            var lst = new List<string>();
                            while (filter.Contains("\""))
                            {
                                var tmpstr = filter.Substring(filter.IndexOf("\""), filter.Substring(filter.IndexOf("\"") + 1).IndexOf("\"") + 2);
                                lst.Add(tmpstr);
                                filter = filter.Replace(tmpstr, "");
                            }
                            if (!string.IsNullOrWhiteSpace(filter))
                            {
                                foreach (var item in filter.Split(' '))
                                {
                                    if (!string.IsNullOrWhiteSpace(item)) lst.Add(item);
                                }
                            }
                            filters = lst.ToArray();
                        }
                        else
                        {
                            filters = filter.Split(' ');
                        }

                        foreach (var item in originalitems)
                        {
                            bool found = false;
                            foreach (var property in filterproperties)
                            {
                                foreach (var term in filters)
                                {
                                    var pval = property.GetValue(item);
                                    if (pval != null
                                        && (
                                                (term.StartsWith("\"") && pval.ToString().Equals(term.Substring(1, term.Length - 2), StringComparison.OrdinalIgnoreCase))
                                            || (!term.StartsWith("\"") && pval.ToString().Contains(term, StringComparison.OrdinalIgnoreCase))
                                            )
                                        )
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                                if (found) break;
                            }
                            if (!found) Items.Remove(item);
                        }
                    }
                    OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
                }
            }
        }
        public void RemoveFilter()
        {
            if (originalitems != null)
            {
                foreach (var item in originalitems)
                {
                    if (!Items.Contains(item)) Items.Add(item);
                }
                OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
            }
        }


        public ListSortDescriptionCollection SortDescriptions => throw new NotImplementedException();

        public bool SupportsAdvancedSorting => throw new NotImplementedException();

        public void ApplySort(ListSortDescriptionCollection sorts)
        {
            throw new NotImplementedException();
        }


    }

}
