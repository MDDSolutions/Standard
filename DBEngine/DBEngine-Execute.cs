using MDDFoundation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
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
