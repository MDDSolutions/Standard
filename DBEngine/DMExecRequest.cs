using System;
using System.Collections.Generic;
using System.Text;

namespace MDDDataAccess
{
    internal class DMExecSession
    {
		public Int16 session_id { get; set; }
		public DateTime login_time { get; set; }
		public String host_name { get; set; }
		public String program_name { get; set; }
		public Int32? host_process_id { get; set; }
		public Int32? client_version { get; set; }
		public String client_interface_name { get; set; }
		public byte[] security_id { get; set; }
		public String login_name { get; set; }
		public String nt_domain { get; set; }
		public String nt_user_name { get; set; }
		public String status { get; set; }
		public byte[] context_info { get; set; }
		public Int32 cpu_time { get; set; }
		public Int32 memory_usage { get; set; }
		public Int32 total_scheduled_time { get; set; }
		public Int32 total_elapsed_time { get; set; }
		public Int32 endpoint_id { get; set; }
		public DateTime last_request_start_time { get; set; }
		public DateTime? last_request_end_time { get; set; }
		public Int64 reads { get; set; }
		public Int64 writes { get; set; }
		public Int64 logical_reads { get; set; }
		public Boolean is_user_process { get; set; }
		public Int32 text_size { get; set; }
		public String language { get; set; }
		public String date_format { get; set; }
		public Int16 date_first { get; set; }
		public Boolean quoted_identifier { get; set; }
		public Boolean arithabort { get; set; }
		public Boolean ansi_null_dflt_on { get; set; }
		public Boolean ansi_defaults { get; set; }
		public Boolean ansi_warnings { get; set; }
		public Boolean ansi_padding { get; set; }
		public Boolean ansi_nulls { get; set; }
		public Boolean concat_null_yields_null { get; set; }
		public Int16 transaction_isolation_level { get; set; }
		public Int32 lock_timeout { get; set; }
		public Int32 deadlock_priority { get; set; }
		public Int64 row_count { get; set; }
		public Int32 prev_error { get; set; }
		public byte[] original_security_id { get; set; }
		public String original_login_name { get; set; }
		public DateTime? last_successful_logon { get; set; }
		public DateTime? last_unsuccessful_logon { get; set; }
		public Int64? unsuccessful_logons { get; set; }
		public Int32 group_id { get; set; }
		[DBOptional]
		public Int16 database_id { get; set; }
		[DBOptional]
		public Int32? authenticating_database_id { get; set; }
		[DBOptional]
		public Int32 open_transaction_count { get; set; }
		public Single? percent_complete { get; set; }
		public Int64? estimated_completion_time { get; set; }
	}
}
