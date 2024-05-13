using System;

namespace MDDDataAccess
{
    //Use DBIgnore for properties you do not want ObjectFromReader to try to automap at all - even if a column with the property name exists, it will not be mapped
    [AttributeUsage(AttributeTargets.Property)]
    public class DBIgnoreAttribute : Attribute { }


    //Use DBOptional if you want ObjectFromReader to try to find a column with the property name, but ignore the property silently if it does not find it
    [AttributeUsage(AttributeTargets.Property)]
    public class DBOptionalAttribute : Attribute { }


    [AttributeUsage(AttributeTargets.Property)]
    public class DBNameAttribute : Attribute
    {
        public string DBName { get; set; }
        public DBNameAttribute(string Name)
        {
            this.DBName = Name;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class DBLoadedTimeAttribute : Attribute { }


    [AttributeUsage(AttributeTargets.Class)]
    public class DBUpsertAttribute : Attribute
    {
        public string UpsertProcName { get; set; }
        public DBUpsertAttribute(string name)
        {
            this.UpsertProcName = name;
        }
    }
    [AttributeUsage(AttributeTargets.Class)]
    public class DBDeleteAttribute : Attribute
    {
        public string DeleteProcName { get; set; }
        public DBDeleteAttribute(string name)
        {
            this.DeleteProcName = name;
        }
    }
    [AttributeUsage(AttributeTargets.Class)]
    public class DBSelectAttribute : Attribute
    {
        public string SelectProcName { get; set; }
        public DBSelectAttribute(string name)
        {
            this.SelectProcName = name;
        }
    }
    [AttributeUsage(AttributeTargets.Class)]
    public class DBSelectByAttribute : Attribute
    {
        public string SelectProcName { get; set; }
        public DBSelectByAttribute(string name)
        {
            this.SelectProcName = name;
        }
    }
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class ListKeyAttribute : Attribute { }
 
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class ListConcurrencyAttribute : Attribute { }

}
