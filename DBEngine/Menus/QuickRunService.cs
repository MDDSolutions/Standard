using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess.Menus
{
    public sealed class QuickRunParameter
    {
        internal SqlParameter Template { get; set; }
        public string Name { get; set; }
        public string TypeDescription { get; set; }
        public string DefaultValue { get; set; }
        public string LastValue { get; set; }
    }
    public sealed class QuickRunDefinition
    {
        public string ProcedureName { get; set; }
        public IReadOnlyList<QuickRunParameter> Parameters { get; set; }
    }
    public sealed class QuickRunValue
    {
        public QuickRunParameter Parameter { get; set; }
        public string Value { get; set; }
        public bool SaveAsDefault { get; set; }
        public bool UseProcedureDefault { get; set; }
    }
    public interface IQuickRunService
    {
        Task<QuickRunDefinition> LoadAsync(string procedureName, CancellationToken token);
        Task ExecuteAsync(QuickRunDefinition definition, IReadOnlyList<QuickRunValue> values, IProgress<string> output, CancellationToken token);
    }
}

namespace MDDDataAccess
{
    using Menus;
    public partial class DBEngine : IQuickRunService
    {
        async Task<QuickRunDefinition> IQuickRunService.LoadAsync(string procedureName, CancellationToken token)
        {
            using (var connection = await getconnectionasync(token, -1, "QuickRun").ConfigureAwait(false))
            {
                if (connection == null) throw new InvalidOperationException("Quick Run requires a configured database connection.");
                string canonical;
                using (var lookup = new SqlCommand("SELECT QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name) FROM sys.procedures WHERE object_id = OBJECT_ID(@name);", connection))
                {
                    lookup.Parameters.Add("@name", SqlDbType.NVarChar, 517).Value = procedureName;
                    canonical = await lookup.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                }
                if (canonical == null) throw new InvalidOperationException($"Stored procedure '{procedureName}' was not found or its metadata is not visible in this database.");
                var parameters = new List<QuickRunParameter>();
                using (var command = new SqlCommand(canonical, connection) { CommandType = CommandType.StoredProcedure })
                {
                    // SqlClient has no asynchronous DeriveParameters; keep metadata discovery off the UI thread.
                    await Task.Run(() => SqlCommandBuilder.DeriveParameters(command), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    foreach (SqlParameter parameter in command.Parameters)
                    {
                        if (parameter.Direction == ParameterDirection.ReturnValue) continue;
                        if (parameter.SqlDbType == SqlDbType.Structured || parameter.SqlDbType == SqlDbType.Udt)
                            throw new NotSupportedException($"Quick Run's text parameter editor does not support {parameter.ParameterName} ({parameter.SqlDbType}).");
                        parameters.Add(new QuickRunParameter { Name = parameter.ParameterName, TypeDescription = parameter.SqlDbType + " " + parameter.Direction, Template = (SqlParameter)((ICloneable)parameter).Clone(),
                            DefaultValue = parameter.Direction == ParameterDirection.InputOutput ? "NULL" : "", LastValue = "" });
                    }
                }
                using (var command = new SqlCommand("SELECT ParameterName, DefaultValue, LastValue FROM MenuSystem.QuickRunParameter WHERE ProcedureName = @name;", connection))
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 517).Value = canonical;
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var parameter = parameters.Find(p => string.Equals(p.Template.ParameterName, reader.GetString(0), StringComparison.OrdinalIgnoreCase));
                            if (parameter == null) continue;
                            parameter.DefaultValue = reader.IsDBNull(1) ? "" : reader.GetString(1);
                            parameter.LastValue = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        }
                }
                return new QuickRunDefinition { ProcedureName = canonical, Parameters = parameters.AsReadOnly() };
            }
        }

        async Task IQuickRunService.ExecuteAsync(QuickRunDefinition definition, IReadOnlyList<QuickRunValue> values, IProgress<string> output, CancellationToken token)
        {
            using (var connection = await getconnectionasync(token, -1, "QuickRun").ConfigureAwait(false))
            {
                if (connection == null) throw new InvalidOperationException("Quick Run requires a configured database connection.");
                using (var command = new SqlCommand(definition.ProcedureName, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 })
                {
                    foreach (var value in values)
                    {
                        if (value.UseProcedureDefault) continue;
                        var parameter = (SqlParameter)((ICloneable)value.Parameter.Template).Clone();
                        try { parameter.Value = QuickRunParameterValue(value.Value, parameter.SqlDbType); }
                        catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentException)
                        { throw new ArgumentException($"Invalid value for {parameter.ParameterName} ({parameter.SqlDbType}): {ex.Message}"); }
                        command.Parameters.Add(parameter);
                    }
                    var returnValue = command.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
                    returnValue.Direction = ParameterDirection.ReturnValue;
                    // Preserve the old runner's last/default values on an execution attempt, atomically across parameters.
                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var value in values)
                        {
                            if (value.UseProcedureDefault) continue;
                            using (var save = new SqlCommand(@"UPDATE MenuSystem.QuickRunParameter WITH (UPDLOCK, SERIALIZABLE)
SET LastValue = @value, DefaultValue = CASE WHEN @default = 1 THEN @value ELSE DefaultValue END
WHERE ProcedureName = @procedure AND ParameterName = @parameter;
IF @@ROWCOUNT = 0 INSERT MenuSystem.QuickRunParameter(ProcedureName, ParameterName, DefaultValue, LastValue)
VALUES(@procedure, @parameter, @value, @value);", connection, transaction))
                            {
                                save.Parameters.Add("@procedure", SqlDbType.NVarChar, 517).Value = definition.ProcedureName;
                                save.Parameters.Add("@parameter", SqlDbType.NVarChar, 128).Value = value.Parameter.Template.ParameterName;
                                save.Parameters.Add("@value", SqlDbType.NVarChar, -1).Value = value.Value ?? "";
                                save.Parameters.Add("@default", SqlDbType.Bit).Value = value.SaveAsDefault;
                                await save.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        transaction.Commit();
                    }
                    SqlInfoMessageEventHandler handler = (s, e) => output?.Report(e.Message);
                    connection.InfoMessage += handler;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await ExecuteNonQueryAsync(command, token).ConfigureAwait(false);
                        foreach (SqlParameter parameter in command.Parameters)
                            if (parameter.Direction != ParameterDirection.Input)
                                output?.Report(parameter.ParameterName + " = " + (parameter.Value == DBNull.Value ? "NULL" : Convert.ToString(parameter.Value)));
                    }
                    finally { connection.InfoMessage -= handler; }
                }
            }
        }
        public static object QuickRunParameterValue(string value, SqlDbType type)
        {
            if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)) return DBNull.Value;
            if (type == SqlDbType.Binary || type == SqlDbType.VarBinary || type == SqlDbType.Image || type == SqlDbType.Timestamp)
            {
                var hex = value ?? "";
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
                if (hex.Length % 2 != 0) throw new FormatException("Binary parameter values must contain pairs of hexadecimal digits.");
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return bytes;
            }
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            switch (type)
            {
                case SqlDbType.Bit: return value == "1" ? true : value == "0" ? false : bool.Parse(value);
                case SqlDbType.UniqueIdentifier: return Guid.Parse(value);
                case SqlDbType.BigInt: return long.Parse(value, culture);
                case SqlDbType.Int: return int.Parse(value, culture);
                case SqlDbType.SmallInt: return short.Parse(value, culture);
                case SqlDbType.TinyInt: return byte.Parse(value, culture);
                case SqlDbType.Decimal:
                case SqlDbType.Money:
                case SqlDbType.SmallMoney: return decimal.Parse(value, culture);
                case SqlDbType.Float: return double.Parse(value, culture);
                case SqlDbType.Real: return float.Parse(value, culture);
                case SqlDbType.Date:
                case SqlDbType.DateTime:
                case SqlDbType.DateTime2:
                case SqlDbType.SmallDateTime: return DateTime.Parse(value, culture);
                case SqlDbType.DateTimeOffset: return DateTimeOffset.Parse(value, culture);
                case SqlDbType.Time: return TimeSpan.Parse(value, culture);
                default: return value ?? "";
            }
        }
    }
}
