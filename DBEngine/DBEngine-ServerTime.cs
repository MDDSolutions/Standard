using System;
using System.Data.SqlClient;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public TimeSpan ServerOffset { get; set; } = TimeSpan.FromTicks(0);
        public DateTime ServerOffsetLastUpdated { get; set; } = DateTime.MinValue;
        public TimeSpan ServerOffsetUpdateInterval { get; set; } = TimeSpan.FromSeconds(60);
        public DateTime NowServer()
        {
            if (DBConnected && ServerOffsetLastUpdated + ServerOffsetUpdateInterval < DateTime.Now)
            {
                UpdateServerOffset(5, "DBEngine.UpdateServerOffset");
            }
            return DateTime.Now + ServerOffset;
        }
        public void UpdateServerOffset(int ConnectionTimeout = -1, string ApplicationName = null)
        {
            using (SqlConnection cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand("SELECT GETDATE();", cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        var start = DateTime.Now;
                        var servertime = (DateTime)ExecuteScalar(cmd);
                        var end = DateTime.Now;
                        LastSQlCommandElapsed = end - start;
                        ServerOffset = servertime - end;
                        ServerOffsetLastUpdated = DateTime.Now;
                    }
                }
            }
        }
    }
}
