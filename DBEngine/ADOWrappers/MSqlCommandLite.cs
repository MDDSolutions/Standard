using MDDDataAccess;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public class MSqlCommandLite : MSqlCommand
    {
        /// <summary>
        /// Provides a simplified SqlCommand wrapper that manages it's own connection
        /// </summary>
        /// <param name="commandText"></param>
        /// <param name="connectionString"></param>
        public MSqlCommandLite(string commandText, string connectionString)
            : base(commandText, new MSqlConnection(connectionString))
        {
            _MSqlConnection.Open();
        }

        // Hide the public Connection property from the base class
        /// <summary>
        /// The Connection property is managed internally and should not be accessed directly.
        /// </summary>
        public override MSqlConnection Connection
        {
            get { throw new InvalidOperationException("The Connection property is managed internally and should not be accessed directly."); }
            set { throw new InvalidOperationException("The Connection property is managed internally and should not be set directly."); }
        }
        // Hide the public UpdatedRowSource property from the base class
        /// <summary>
        /// The UpdatedRowSource property is not relevant for the MSqlCommand object.
        /// </summary>
        private new UpdateRowSource UpdatedRowSource
        {
            get { throw new InvalidOperationException("The UpdatedRowSource property is not relevant for the MSqlCommand object."); }
            set { throw new InvalidOperationException("The UpdatedRowSource property is not relevant for the MSqlCommand object."); }
        }

        public new void Dispose()
        {
            base.Dispose();
            _MSqlConnection?.Dispose();
        }
    }
}