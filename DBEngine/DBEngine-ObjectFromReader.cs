using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Text;
using System.ComponentModel;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public void ObjectFromReader<T>(SqlDataReader rdr, ref List<Tuple<Action<object, object>, string>> map, ref PropertyInfo key, ref T r, ref IObjectTracker tracker) where T : new()
        {
            if (r == null) r = new T();

            bool nomap = false;

            if (map == null)
            {
                map = new List<Tuple<Action<object, object>, string>>();

                // EnsureCorrectPropertyUsage doesn't work - maybe fix it another day
                //if (Tracking != ObjectTracking.None)
                //{
                //    if (r is INotifyPropertyChanged npc)
                //    {
                //        npc.EnsureCorrectPropertyUsage();
                //    }
                //}


                if (Tracking == ObjectTracking.Full || (Tracking == ObjectTracking.IfAvailable && r is ITrackedEntity))
                {
                    tracker = GetOrCreateTracker<T>() as IObjectTracker;
                }


                foreach (var item in r.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    bool include = true;
                    string DBName = null;
                    bool optional = false;
                    foreach (var attr in item.GetCustomAttributes(true))
                    {
                        if (attr is DBIgnoreAttribute)
                            include = false;
                        if (attr is DBNameAttribute dbna)
                            DBName = dbna.DBName;
                        if (attr is DBOptionalAttribute)
                            optional = true;
                        if (attr is ListKeyAttribute)
                            key = item;
                        if (attr is DBLoadedTimeAttribute)
                        {
                            include = false;
                            nomap = true;
                            map = null;
                            item.SetValue(r, DateTime.Now);
                        }

                    }

                    if (include && item.CanWrite && item.ToString().StartsWith("System.Nullable`1[System.Char]"))
                    {
                        nomap = true;
                        map = null;

                        object o = null;
                        if (DBName != null)
                            o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
                        else
                            o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
                        if (o != null)
                            item.SetValue(r, ((string)o)[0]);
                        else
                            item.SetValue(r, default);

                    }
                    else if (include && item.CanWrite && item.PropertyType.BaseType?.Name != "Enum" && (item.PropertyType.IsValueType || item.PropertyType.Name == "String" || item.PropertyType.Name == "Byte[]"))
                    {
                        try
                        {
                            if (DBName == null) DBName = item.Name;
                            bool optcolfound = false;
                            if (optional)
                            {
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    if (rdr.GetName(i).Equals(DBName, StringComparison.InvariantCultureIgnoreCase))
                                        optcolfound = true;
                                }
                            }

                            if (!optional || optcolfound)
                            {
                                object o = null;

                                if (r is StringObj)
                                {
                                    o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
                                    if (!nomap) map.Add(new Tuple<Action<object, object>, string>(BuildSetAccessor(item.GetSetMethod(true)), rdr.GetName(0)));
                                }
                                else if (DBName != null)
                                {
                                    o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
                                    if (!nomap) map.Add(new Tuple<Action<object, object>, string>(BuildSetAccessor(item.GetSetMethod(true)), DBName));
                                }
                                //else
                                //{
                                //    o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
                                //    if (!nomap) map.Add(new Tuple<Action<object, object>, string>(BuildSetAccessor(item.GetSetMethod()), item.Name));
                                //}
                                if (o != null)
                                    item.SetValue(r, o);
                                else
                                    item.SetValue(r, default);
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            if (!optional)
                                throw new Exception($"DBEngine internal error: The column '{DBName ?? item.Name}' was specified as a property (or DBName attribute) in the '{r.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"DBEngine internal error: Error occurred mapping property '{DBName ?? item.Name}' in the '{r.GetType().Name}' object - see inner exception", ex);
                        }
                    }
                    else if (include && item.CanWrite && item.PropertyType.BaseType?.Name == "Enum")
                    {
                        nomap = true;
                        map = null;

                        object o = null;
                        if (DBName != null)
                            o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
                        else if (r is StringObj)
                            o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
                        else
                            o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
                        if (o != null)
                        {
                            if (o is int)
                                item.SetValue(r, Enum.ToObject(item.PropertyType, o));
                            else
                                item.SetValue(r, Enum.Parse(item.PropertyType, o.ToString()));
                        }
                        else
                            item.SetValue(r, default);
                    }
                    else if (include && item.CanWrite && item.PropertyType is ISerializable && !item.PropertyType.FullName.Contains("System"))
                    {   //this is meant to handle properties that are serializable user types - i.e. classes that implement ISerializable (or are just decorated with [Serializable])
                        //it may be possible to implement an Action<object, object> method that does this but what I'm doing here is disabling the "map" and forcing execution to go
                        //through the non-mapped code here for every row - I don't think that was much worse for performance anyway, but if you want to automatically handle complex
                        //types then something's got to give...

                        // This is mostly untested at this point...

                        nomap = true;
                        map = null;

                        try
                        {

                            byte[] o = null;
                            if (DBName != null)
                                o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName] as byte[];
                            else if (r is StringObj)
                                o = Convert.IsDBNull(rdr[0]) ? null : rdr[0] as byte[];
                            else
                                o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name] as byte[];
                            if (o != null)
                            {
                                var formatter = new BinaryFormatter();
                                using (var stream = new MemoryStream(o))
                                {
                                    //if the byte array is a valid serialization of the type of the property, this will work
                                    //...if not, this will probably puke considerably...
                                    var o2 = formatter.Deserialize(stream);
                                    item.SetValue(r, o2);
                                }
                            }
                            else
                            {
                                item.SetValue(r, default);
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"DBEngine internal error: Error occurred mapping *complex* property '{DBName ?? item.Name}' in the '{r.GetType().Name}' object - see inner exception", ex);
                        }
                    }
                }

            }
            else
            {
                int len = map.Count;
                object o = null;
                for (int i = 0; i < len; i++)
                {
                    try
                    {
                        o = Convert.IsDBNull(rdr[map[i].Item2]) ? null : rdr[map[i].Item2];
                        map[i].Item1?.Invoke(r, o);
                    }
                    catch (Exception ex)
                    {
                        if (o == null) o = "<null>";
                        throw new Exception($"DBEngine internal error: Post-mapping error occurred trying to set {r.GetType().Name}.{map[i].Item2} to {o} which is a {o.GetType().Name} (the data types must match exactly) - see inner exception", ex);
                    }
                }
            }
            if (Tracking != ObjectTracking.None)
            {
                if (r is ITrackedEntity npc)
                {
                    npc.EndInit();
                    if (tracker != null) { r = (T)tracker.Load(r); }
                }
            }
            //if (Tracking == ObjectTracking.ChangeNotification)
            //{
            //    if (r is INotifyPropertyChanged notifier)
            //    {
            //        var aggregator = GetOrCreateEventAggregator<T>();
            //        aggregator.QuickSubscribe(notifier);
            //    }
            //    else
            //    {
            //        throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but type '{r.GetType().Name}' does not implement INotifyPropertyChanged");
            //    }
            //}
        }
    }
}
