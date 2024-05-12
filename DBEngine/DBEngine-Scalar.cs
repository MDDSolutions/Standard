using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public async Task<Object> SqlGetScalarAsync(string cmdText, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            if (!IsProcedure && (list == null || list.Length == 0) && !cmdText.Any(x => Char.IsWhiteSpace(x)))
            {
                list = new SqlParameter[] { new SqlParameter("@ParamName", cmdText) };
                cmdText = "SELECT ParamVal FROM dbo.Parameters WHERE ParamName = @ParamName";
            }
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = await getconnectionasync(CancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdText, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = System.Data.CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        var dt = DateTime.Now;
                        var o = await cmd.ExecuteScalarAsync(CancellationToken).ConfigureAwait(false);
                        LastSQlCommandElapsed = DateTime.Now - dt;
                        return o;
                    }
                }
                else
                    return null;
            }
        }
        public Object SqlGetScalar(string cmdText, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdText, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = System.Data.CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        var dt = DateTime.Now;
                        var o = ExecuteScalar(cmd);
                        LastSQlCommandElapsed = DateTime.Now - dt;
                        return o;
                    }
                }
                else
                    return null;
            }
        }
        public async Task<T> SqlGetScalarAsync<T>(string cmdText, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            var x = await SqlGetScalarAsync(cmdText, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, list).ConfigureAwait(false);
            if (x == DBNull.Value || x == null)
                return default(T);
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
                return (T)converter.ConvertFrom(x.ToString());
            throw new Exception($"Unable to get a converter for '{typeof(T)}'");
        }
        public T SqlGetScalar<T>(string cmdText, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            var x = SqlGetScalar(cmdText, IsProcedure, ConnectionTimeout, ApplicationName, list);
            if (x == DBNull.Value)
                return default(T);
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                if (x == null)
                    return default(T);
                if (converter.CanConvertFrom(x.GetType()))
                    return (T)converter.ConvertFrom(x);
                else
                    return (T)converter.ConvertFrom(x.ToString());
            }
            throw new Exception($"Unable to get a converter for '{typeof(T)}'");
        }
        public T SqlGetScalarStub<T>(string cmdText, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list)
        {
            return SqlGetScalar<T>(cmdText, IsProcedure, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
        public Decimal ParseOrEvaluateDecimal(string Expression)
        {
            bool OrigAllowAdhoc = AllowAdHoc;
            Expression = Expression.TrimStart('=');
            if (Decimal.TryParse(Expression, out decimal d))
            {
                return d;
            }
            else
            {
                try
                {
                    char[] InvalidCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ;".ToCharArray();
                    if (Expression.IndexOfAny(InvalidCharacters) != -1)
                        throw new Exception("Invalid characters detected");
                    AllowAdHoc = true;
                    return Convert.ToDecimal(DBEngine.Default.SqlGetScalar(string.Format("SELECT {0};", Expression), false, -1, null));
                }
                catch (Exception)
                {
                    throw;
                }
                finally
                {
                    AllowAdHoc = OrigAllowAdhoc;
                }
            }
        }
    }
}
