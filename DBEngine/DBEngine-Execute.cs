using MDDFoundation;
using System;
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
        public bool Debug { get; set; }
        public const string LogFileName = "DBEngine_log.txt";

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
            Stopwatch sw = null;
            if (KeepStats)
            {
                sw = new Stopwatch();
                sw.Start();
            }
            try
            {
                var rdr = cmd.ExecuteReader();
                if (KeepStats)
                {
                    sw.Stop();
                    CommandStat.RecordStat(cmd.CommandText, sw.Elapsed);
                    if (Debug) Debugcmd(cmd, sw);
                }
                return rdr;
            }
            catch (Exception ex) 
            {
                if (LogErrors)
                {
                    Foundation.Log("*****************************************************************", false, LogFileName);
                    Foundation.Log("DBEngine command execution error in ExecuteReader:",false,LogFileName);
                    Foundation.Log(ex.ToString(),false,LogFileName);
                    Foundation.Log("Command Text:", false, LogFileName);
                    Debugcmd(cmd, sw);
                    Foundation.Log("*****************************************************************", false, LogFileName);
                }
                throw ex;
            }
        }
        private async Task<SqlDataReader> ExecuteReaderAsync(SqlCommand cmd, CancellationToken token)
        {
            Stopwatch sw = null;
            if (KeepStats)
            {
                sw = new Stopwatch();
                sw.Start();
            }
            try
            {
                var rdr = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (KeepStats)
                {
                    sw.Stop();
                    CommandStat.RecordStat(cmd.CommandText, sw.Elapsed);
                    if (Debug) Debugcmd(cmd, sw);
                }
                return rdr;
            }
            catch (Exception ex)
            {
                if (LogErrors)
                {
                    Foundation.Log("*****************************************************************", false, LogFileName);
                    Foundation.Log("DBEngine command execution error in ExecuteReaderAsync:", false, LogFileName);
                    Foundation.Log(ex.ToString(), false, LogFileName);
                    Foundation.Log("Command Text:", false, LogFileName);
                    Debugcmd(cmd, sw);
                    Foundation.Log("*****************************************************************", false, LogFileName);
                }
                throw ex;
            }
        }
        private async Task ExecuteNonQueryAsync(SqlCommand cmd, CancellationToken token)
        {
            Stopwatch sw = null;
            if (KeepStats)
            {
                sw = new Stopwatch();
                sw.Start();
            }
            try
            {
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (KeepStats)
                {
                    sw.Stop();
                    CommandStat.RecordStat(cmd.CommandText, sw.Elapsed);
                    if (Debug) Debugcmd(cmd, sw);
                }
            }
            catch (Exception ex)
            {
                if (LogErrors)
                {
                    Foundation.Log("*****************************************************************", false, LogFileName);
                    Foundation.Log("DBEngine command execution error in ExecuteNonQueryAsync:", false, LogFileName);
                    Foundation.Log(ex.ToString(), false, LogFileName);
                    Foundation.Log("Command Text:", false, LogFileName);
                    Debugcmd(cmd, sw);
                    Foundation.Log("*****************************************************************", false, LogFileName);
                }
                throw ex;
            }
        }
        private void ExecuteNonQuery(SqlCommand cmd)
        {
            Stopwatch sw = null;
            if (KeepStats)
            {
                sw = new Stopwatch();
                sw.Start();
            }
            try
            {
                cmd.ExecuteNonQuery();
                if (KeepStats)
                {
                    sw.Stop();
                    CommandStat.RecordStat(cmd.CommandText, sw.Elapsed);
                    if (Debug) Debugcmd(cmd, sw);
                }
            }
            catch (Exception ex)
            {
                if (LogErrors)
                {
                    Foundation.Log("*****************************************************************", false, LogFileName);
                    Foundation.Log("DBEngine command execution error in ExecuteNonQuery:", false, LogFileName);
                    Foundation.Log(ex.ToString(), false, LogFileName);
                    Foundation.Log("Command Text:", false, LogFileName);
                    Debugcmd(cmd, sw);
                    Foundation.Log("*****************************************************************", false, LogFileName);
                }
                throw ex;
            }
        }
        private object ExecuteScalar(SqlCommand cmd)
        {
            Stopwatch sw = null;
            if (KeepStats)
            {
                sw = new Stopwatch();
                sw.Start();
            }
            try
            {
                var obj = cmd.ExecuteScalar();
                if (KeepStats)
                {
                    sw.Stop();
                    CommandStat.RecordStat(cmd.CommandText, sw.Elapsed);
                    if (Debug) Debugcmd(cmd, sw);
                }
                return obj;
            }
            catch (Exception ex)
            {
                if (LogErrors)
                {
                    Foundation.Log("*****************************************************************", false, LogFileName);
                    Foundation.Log("DBEngine command execution error in ExecuteScalar:", false, LogFileName);
                    Foundation.Log(ex.ToString(), false, LogFileName);
                    Foundation.Log("Command Text:", false, LogFileName);
                    Debugcmd(cmd, sw);
                    Foundation.Log("*****************************************************************", false, LogFileName);
                }
                throw ex;
            }
        }
        private async Task<object> ExecuteScalarAsync(SqlCommand cmd, CancellationToken token)
        {
            Stopwatch sw = null;
            if (KeepStats)
            {
                sw = new Stopwatch();
                sw.Start();
            }
            try
            {
                var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (KeepStats)
                {
                    sw.Stop();
                    CommandStat.RecordStat(cmd.CommandText, sw.Elapsed);
                    if (Debug) Debugcmd(cmd, sw);
                }
                return obj;
            }
            catch (Exception ex)
            {
                if (LogErrors)
                {
                    Foundation.Log("*****************************************************************", false, LogFileName);
                    Foundation.Log("DBEngine command execution error in ExecuteScalarAsync:", false, LogFileName);
                    Foundation.Log(ex.ToString(), false, LogFileName);
                    Foundation.Log("Command Text:", false, LogFileName);
                    Debugcmd(cmd, sw);
                    Foundation.Log("*****************************************************************", false, LogFileName);
                }
                throw ex;
            }
        }
        private void Debugcmd(SqlCommand cmd, Stopwatch sw)
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
            Foundation.Log(sb.ToString(), false, LogFileName);
        }
    }
}
