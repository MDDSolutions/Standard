using System;
using System.Data;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;

namespace MDDDataAccess
{
    public class DependencyList<T> where T: class, new()
    {

        // This whole thing is broken - a bunch of stuff needed to be commented out to make other stuff work
        // If you ever want to use this again, it needs to be fixed / re-imagined


        private DBEngine engine;
        public DependencyList(DBEngine db)
        {
            if (!db.SqlDependencyStarted)
            {
                SqlDependency.Start(db.ConnectionString.ConnectionString);
                db.SqlDependencyStarted = true;
            }
            engine = db;
        }
        public List<T> List { get; set; }
        public bool Started { get; set; }
        public List<T> Start(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            _cmdtext = cmdtext;
            _IsProcedure = IsProcedure;
            _CancellationToken = CancellationToken;
            _ConnectionTimeout = ConnectionTimeout;
            _ApplicationName = ApplicationName;
            _sqlParameters = list;
            List = Run();
            Started = true;
            return List;
        }
        private string _cmdtext;
        private bool _IsProcedure;
        private CancellationToken _CancellationToken;
        private int _ConnectionTimeout;
        private string _ApplicationName;
        private SqlParameter[] _sqlParameters;
        private List<T> Run()
        {
            if (!_IsProcedure && !engine.AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = engine.GetConnection(_ConnectionTimeout, _ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(_cmdtext, cn))
                    {
                        cmd.CommandTimeout = engine.CommandTimeout;
                        if (_IsProcedure) cmd.CommandType = CommandType.StoredProcedure;

                        DBEngine.ParameterizeCommand(_sqlParameters, cmd);
                        try
                        {
                            cmd.Notification = null;
                            var dependency = new SqlDependency(cmd);
                            dependency.OnChange += Dependency_OnChange;

                            var l = new List<T>();
                            //PropertyInfo key = null;
                            //List<Tuple<PropertyInfo, String>> map = null;
                            //List<Tuple<Action<object, object>, String>> map = null;
                            Console.WriteLine($"{DateTime.Now.ToString("hh:mm:ss.ff")}: Query Running...");
                            using (SqlDataReader rdr = cmd.ExecuteReader())
                            {
                                while (rdr.Read())
                                {
                                    //l.Add((T)Activator.CreateInstance(typeof(T), rdr));
                                    var r = new T();
                                    //DBEngine.ObjectFromReader<T>(rdr, ref map, ref key, ref r);
                                    l.Add(r);
                                    //l.Add(ObjectFromReader<T>(rdr));
                                }
                            }


                            return l;
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return null;
        }

        private void Dependency_OnChange(object sender, SqlNotificationEventArgs e)
        {
            if (e.Info == SqlNotificationInfo.Invalid)
                throw new Exception("DependencyList is Invalid");
            if (e.Type == SqlNotificationType.Subscribe)
                throw new Exception("The statement for the DependencyList does not meet the requirements - reminder... there are very many very specific requirements... https://docs.microsoft.com/en-us/previous-versions/sql/sql-server-2008-r2/ms181122(v=sql.105)?redirectedfrom=MSDN");
            Console.WriteLine($"{DateTime.Now.ToString("hh:mm:ss.ff")}: {e.Info.ToString()} {e.Source.ToString()} {e.Type.ToString()}");
            if (e.Info != SqlNotificationInfo.Query || e.Source != SqlNotificationSource.Statement || e.Type != SqlNotificationType.Subscribe)
            {
                var dependency = sender as SqlDependency;
                dependency.OnChange -= Dependency_OnChange;
                List = Run();
            }
        }
    }
}
