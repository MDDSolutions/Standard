using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Text;
using System.ComponentModel;
using MDDFoundation;
using System.Linq;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public bool EnumParseIgnoreCase { get; set; } = true;
        public void ObjectFromReader<T>(SqlDataReader rdr, ref List<Tuple<Action<object, object>, string>> map, ref PropertyInfo key, ref T r, ref IObjectTracker tracker, bool strict = true) where T : new()
        {
            if (r == null) r = new T();

            Tuple<PropertyInfo, bool> keyinfo = null;
            PropertyInfo concurrency = null;
            bool creating = true;
            if (Tracking != ObjectTracking.None && r is ITrackedEntity ite)
            {
                // if tracking is enabled, we need to see if the object already exists in the tracker

                // if method is being called in a loop, tracker will already be set
                if (tracker == null) tracker = GetOrCreateTracker<T>() as IObjectTracker;

                ///KeyInfo returns a tuple of (PropertyInfo for the property marked with ListKeyAttribute, bool indicating whether or not the property has a value)
                ///if the property has a value, then the object should have already been created via the tracker - if it wasn't, that's an error
                keyinfo = AttributeInfo(r, typeof(ListKeyAttribute));
                if (keyinfo.Item1 == null)
                    throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but type '{r.GetType().Name}' does not have a property marked with ListKeyAttribute");
                if (keyinfo.Item2)
                {
                    //if we are explicity providing a dirty object then chances are this is an update operation so we let dirty objects through
                    //on the other hand, there should be some kind of indicator that the object is in the middle of a save operation so we don't 
                    //have to assume here - perhaps a BeginSave / EndSave in IObjectTracker?
                    //if (ite.IsDirty)
                    //{
                    //    //could just quietly return here but depending on why we're querying, there might be confusion as to why we're not loading the values we queried...
                    //    //return;
                    //    throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but the object being loaded is already being tracked and is dirty - you must either set Tracking to None or ensure that all objects being loaded are not already being tracked as dirty");
                    //}
                    if (tracker.Exists(r))
                    {
                        creating = false;
                        ite.BeginUpdate();
                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 55, "OFR cache hit", r.ToString(), typeof(T).Name));
                    }
                    else
                    {
                        throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but the object being loaded is not being tracked - you must either set Tracking to None or ensure that all objects being loaded are first created via the tracker");
                    }
                }
                else // no key value so we need to get the key value from the reader and see if the object exists in the tracker
                {
                    object keyvalue = rdr[keyinfo.Item1.Name];
                    if (keyvalue == null || keyvalue == DBNull.Value)
                        throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but the object being loaded has a null key value - you must either set Tracking to None or ensure that all objects being loaded have a valid key value");
                    var newObj = r;
                    r = (T)tracker.Retrieve(keyvalue, r);
                    if (!ReferenceEquals(r,newObj))
                    {
                        ite = r as ITrackedEntity;
                        if (ite.IsDirty)
                        {
                            //could just quietly return here but depending on why we're querying, there might be confusion as to why we're not loading the values we queried...
                            //return;
                            throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but the object being loaded is already being tracked and is dirty - you must either set Tracking to None or ensure that all objects being loaded are not already being tracked as dirty");
                        }
                        creating = false;
                        ite.BeginUpdate();
                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 53, "OFR cache update", r.ToString(), typeof(T).Name));
                    }
                    else
                    {
                        creating = true;
                        ite.BeginInit();
                        if (DebugLevel >= 220) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 51, "OFR create", $"Type: {typeof(T).Name} ID: {keyvalue}", typeof(T).Name));
                    }
                }


                //else
                //{
                //    creating = true;
                //    ite.BeginInit();
                //}
            }

            if (!strict)
            {
                //non-strict mode means we don't match all properties to reader columns but it is still pretty strict... the object must have a populated Key property and a concurrency property
                //this method assumes that the database operation is checking the concurrency property but cannot ensure that
                if (keyinfo == null)
                   keyinfo = AttributeInfo(r, typeof(ListKeyAttribute));
                if (keyinfo.Item1 == null || !keyinfo.Item2)
                    throw new Exception($"DBEngine error: Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListKeyAttribute and that the property have a value");

                concurrency = AttributeProperty<T>(typeof(ListConcurrencyAttribute));
                if (concurrency == null)
                    throw new Exception($"DBEngine error: Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListConcurrencyAttribute");

                //at this point all we need to do is not throw an error if properties are missing from the reader - we still map everything we can find - but we do need to make sure
                //that the concurrency property *is* in the reader
            }


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





                foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
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

                    // in non-strict mode, all properties are optional except the concurrency property
                    // the key property isn't optional either but we've already checked for it above
                    // even if the concurrency property is marked with DBOptional, we still require it to be in the reader in non-strict mode
                    if (!strict) optional = item != concurrency;

                    if (include && item.CanWrite && (item.PropertyType.Name == "Char" || item.ToString().StartsWith("System.Nullable`1[System.Char]")))
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
                                item.SetValue(r, Enum.Parse(item.PropertyType, o.ToString(), EnumParseIgnoreCase));
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

            if (Tracking != ObjectTracking.None && r is ITrackedEntity ite2)
            {
                if (creating)
                {
                    ite2.EndInit();
                    if (tracker != null) { r = (T)tracker.Load(r); }
                }
                else
                {
                    ite2.EndUpdate();
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
