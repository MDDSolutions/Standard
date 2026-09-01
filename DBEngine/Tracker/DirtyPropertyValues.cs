namespace MDDDataAccess
{
    public readonly struct DirtyPropertyValues
    {
        public DirtyPropertyValues(object oldValue, object newValue)
        {
            OldValue = oldValue;
            NewValue = newValue;
        }

        public object OldValue { get; }
        public object NewValue { get; }

        public void Deconstruct(out object oldValue, out object newValue)
        {
            oldValue = OldValue;
            newValue = NewValue;
        }
    }
}
