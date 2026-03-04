using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Text;

namespace MDDDataAccess
{
    public static class DBParameter
    {
        public static DBEngine DB { get; set; } = DBEngine.Default;
        public static string GetCMD { get; set; } = "sp_parameter_get";
        public static string SetCMD { get; set; } = "sp_parameter_set";
        public static bool IsProcedure { get; set; } = true;
        public static string NameParamName { get; set; } = "@ParamName";
        public static string ValParamName { get; set; } = "@ParamVal";
        public static List<Tuple<string,string>> Replacements { get; set; } = new List<Tuple<string, string>>();
        public static void AddReplacement(string find, string replace)
        {
            if (Replacements == null)
                Replacements = new List<Tuple<string, string>>();
            var current = Replacements.Find(r => r.Item1 == find);
            if (current != null)
                Replacements.Remove(current);
            Replacements.Add(new Tuple<string, string>(find, replace));
        }
        public static Func<string, string> NormalizeParamName { get; set; } = DefaultNormalize;
        static string DefaultNormalize(string paramname)
        {
            if (paramname.StartsWith("txt") || paramname.StartsWith("chk") || paramname.StartsWith("ctl"))
                return paramname.Substring(3);
            return paramname;
        }
        public static string? GetParamValue(string paramname, bool noerror = false)
        {
            try
            {
                var paramval = new SqlParameter("@ParamVal", SqlDbType.VarChar, 255);
                paramval.Direction = ParameterDirection.Output;
                DB.SqlRunProcedure(GetCMD, -1, null,
                    new SqlParameter(NameParamName, NormalizeParamName(paramname)),
                    new SqlParameter("@AutofillCurrentParameters", false),
                    paramval);
                var retval = paramval.Value == DBNull.Value ? null : paramval.Value.ToString();
                foreach (var replacement in Replacements)
                    retval = retval?.Replace(replacement.Item1, replacement.Item2);
                return retval;
            }
            catch (Exception ex)
            {
                if (noerror)
                {
                    return null;
                }
                else
                {
                    throw new Exception($"Error getting parameter value for {paramname}", ex);
                }
            }
        }
        public static void SetParamValue(string paramname, object paramval)
        {
            DBEngine.Default.SqlRunProcedure(SetCMD, -1, null,
                new SqlParameter(NameParamName, NormalizeParamName(paramname)),
                new SqlParameter(ValParamName, paramval));
        }
    }
}
