using MDDFoundation;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public bool KeepStats { get; set; }
        public bool LogErrors { get; set; }
        public byte DebugLevel { get; set; } = 0;
        //public const string LogFileName = "DBEngine_log.txt";
        public RichLog Log { get; set; } = new RichLog("DBEngine",null);

        public void ExecuteScript(string script)
        {
            if (AllowAdHoc)
            {
                // Remove block comments
                string blockComments = @"/\*(.*?)\*/";
                script = Regex.Replace(script, blockComments, "", RegexOptions.Singleline);

                // Split script into separate commands
                string[] commands = Regex.Split(script, @"(?<=^|[\r\n])\s*GO\s*($|[\r\n])", RegexOptions.Multiline | RegexOptions.IgnoreCase);

                using (SqlConnection connection = getconnection())
                {
                    foreach (string command in commands)
                    {
                        string trimmedCommand = command.Trim();
                        if (!string.IsNullOrEmpty(trimmedCommand))
                        {
                            using (SqlCommand sqlCommand = new SqlCommand(trimmedCommand, connection))
                            {
                                ExecuteNonQuery(sqlCommand);
                            }
                        }
                    }
                }
            }
            else
            {
                throw new Exception("AdHoc commands are not allowed by this DBEngine");
            }
        }
        public async Task ExecuteScriptAsync(string script, CancellationToken token)
        {
            if (AllowAdHoc)
            {
                // Remove block comments
                string blockComments = @"/\*(.*?)\*/";
                script = Regex.Replace(script, blockComments, "", RegexOptions.Singleline);

                // Split script into separate commands
                string[] commands = Regex.Split(script, @"(?<=^|[\r\n])\s*GO\s*($|[\r\n])", RegexOptions.Multiline | RegexOptions.IgnoreCase);

                using (SqlConnection connection = getconnection())
                {
                    foreach (string command in commands)
                    {
                        if (token.IsCancellationRequested)
                        {
                            using (SqlCommand cmd = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK;", connection))
                            {
                                await ExecuteNonQueryAsync(cmd, CancellationToken.None).ConfigureAwait(false);
                            }
                            break;
                        }
                        string trimmedCommand = command.Trim();
                        if (!string.IsNullOrEmpty(trimmedCommand))
                        {
                            using (SqlCommand sqlCommand = new SqlCommand(trimmedCommand, connection))
                            {
                                await ExecuteNonQueryAsync(sqlCommand, token).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            else
            {
                throw new Exception("AdHoc commands are not allowed by this DBEngine");
            }
        }
        private SqlDataReader ExecuteReader(SqlCommand cmd)
        {
            var start = Environment.TickCount;
            try
            {
                var rdr = cmd.ExecuteReader();
                PostExecution(cmd, start);
                return rdr;
            }
            catch (Exception ex)
            {
                if (LogErrors) LogError(cmd, start, ex);
                throw ex;
            }
        }
        private async Task<SqlDataReader> ExecuteReaderAsync(SqlCommand cmd, CancellationToken token)
        {
            var start = Environment.TickCount;
            try
            {
                var rdr = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                PostExecution(cmd, start);
                return rdr;
            }
            catch (Exception ex)
            {
                if (LogErrors) LogError(cmd, start, ex);
                throw ex;
            }
        }
        private async Task ExecuteNonQueryAsync(SqlCommand cmd, CancellationToken token)
        {
            var start = Environment.TickCount;
            try
            {
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                PostExecution(cmd, start);
            }
            catch (Exception ex)
            {
                if (LogErrors) LogError(cmd, start, ex);
                throw ex;
            }
        }

        private void PostExecution(SqlCommand cmd, int start)
        {
            var elapsed = Environment.TickCount - start;
            if (KeepStats) CommandStat.RecordStat(cmd.CommandText, elapsed);
            if (DebugLevel >= 50)
            {
                Log.Entry(new DBExecutionEntry
                {
                    Source = "Execution",
                    Timestamp = DateTime.Now,
                    Message = cmd.CommandText,
                    Elapsed = elapsed,
                    Severity = DBExecutionEntry.CalculateSeverity(elapsed),
                    Details = DebugLevel >= 100 ? PrintExecStatement(cmd) : Debugcmd(cmd, null)
                }, 2);
            }
        }

        private void ExecuteNonQuery(SqlCommand cmd)
        {
            var start = Environment.TickCount;
            try
            {
                cmd.ExecuteNonQuery();
                PostExecution(cmd, start);
            }
            catch (Exception ex)
            {
                if (LogErrors) LogError(cmd, start, ex);
                throw ex;
            }
        }

        private void LogError(SqlCommand cmd, int start, Exception ex)
        {
            Log.Entry(new DBExecutionEntry
            {
                Source = "Error",
                Timestamp = DateTime.Now,
                Message = ex.Message,
                Elapsed = Environment.TickCount - start,
                Severity = 16,
                Details = PrintExecStatement(cmd)
            }, 2);
        }

        private object ExecuteScalar(SqlCommand cmd)
        {
            var start = Environment.TickCount;
            try
            {
                var obj = cmd.ExecuteScalar();
                PostExecution(cmd, start);
                return obj;
            }
            catch (Exception ex)
            {
                if (LogErrors) LogError(cmd, start, ex);
                throw ex;
            }
        }
        private async Task<object> ExecuteScalarAsync(SqlCommand cmd, CancellationToken token)
        {
            var start = Environment.TickCount;
            try
            {
                var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                PostExecution(cmd, start);
                return obj;
            }
            catch (Exception ex)
            {
                if (LogErrors) LogError(cmd, start, ex);
                throw ex;
            }
        }
        private string Debugcmd(SqlCommand cmd, Stopwatch sw)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"DBEngine executed {cmd.CommandText}");

            foreach (SqlParameter item in cmd.Parameters)
            {
                sb.Append(' ', 25);
                sb.AppendLine($"{item.ParameterName}: {item.Value}");
            }

            sb.Append(' ', 25);
            if (sw != null)
                sb.Append($"execution took {sw.Elapsed}");

            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: {sb}");
            //Foundation.Log(sb.ToString(), false, LogFileName);
            return sb.ToString();
        }
    }
    public class DBExecutionEntry : RichLogEntry
    {
        public int Elapsed { get; set; }
        public static byte CalculateSeverity(int elapsed)
        {
            double k = 200.0 / Math.Log(301.0); // ≈ 35.044
            double severity = k * Math.Log(1 + elapsed / 100.0);
            return (byte)Math.Min(255, Math.Floor(severity));
        }
    }
}
