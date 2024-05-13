using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MDDNetComm
{
    public delegate void ProcessResponseDelegate(ServerTracker tracker, CommMessage cm);
    public class TcpClientComm
    {
        public event EventHandler<ChangeType> ConnectionsChanged;
        public TcpClientComm(string inApplicationName)
        {
            ApplicationName = inApplicationName;
            Util.Log($"TcpClientComm - ApplicationID is {ApplicationID}");
        }
        public string ApplicationName { get; set; }
        public Guid ApplicationID { get; set; } = Guid.NewGuid();
        public int ReadTimeout { get; set; } = 5000;
        public ProcessResponseDelegate ProcessResponseMethod { get; set; }
        private ConcurrentDictionary<Guid, ServerTracker> trackers { get; set; } = new ConcurrentDictionary<Guid, ServerTracker>();
        public ServerTracker ServerTrackerGetOrAdd(Guid key, ServerTracker value)
        {
            value.Connected = true;
            bool added = false;
            var retval = trackers.GetOrAdd(key, (k) => { added = true; return value; });
            if (added)
            {
                Util.Log($"Reconnected to server: {retval}");
                ConnectionsChanged?.Invoke(retval, ChangeType.Add);
            }
            return retval;
        }
        public ServerTracker ServerTrackerGetOrAdd(Guid key, IPEndPoint ep, string SourceMachine)
        {
            bool added = false;
            var st = trackers.GetOrAdd(key, (k) => { added = true; return new ServerTracker(key, ep, SourceMachine); });
            st.Connected = true;
            if (added)
            {
                Util.Log($"Connected to server: {st}");
                ConnectionsChanged?.Invoke(st, ChangeType.Add);
            }
            return st;
        }
        public bool ServerTrackerRemove(Guid key)
        {
            if (!(trackers.TryGetValue(key, out ServerTracker st)))
                return false;
            st.Connected = false;
            Util.Log($"Disconnected from server: {st}");
            ConnectionsChanged?.Invoke(st, ChangeType.Remove);
            return true;
        }
        public IEnumerable<ServerTracker> ServerTrackers()
        {
            return trackers.Values.Where(x => x.Connected);
        }

        //public event EventHandler<Exception> Error;
        //public event EventHandler<string> DisplayMessage;
        public async Task<ServerTracker> TcpConnect(string HostName, int PortNumber)
        {
            ServerTracker st = trackers.Where(x => x.Value.MachineName.Equals(HostName, StringComparison.OrdinalIgnoreCase) && x.Value.IPEndPoint.Port == PortNumber).FirstOrDefault().Value;
            if (st != null)
            {
                return st;
            }
            var client = new TcpClient();
            var result = client.BeginConnect(HostName, PortNumber, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            if (!success) throw new Exception($"TcpClientComm.TcpConnect: Timeout trying to connect to {HostName}:{PortNumber}");
            var response = await SendMessage(client, new CommMessage(), st).ConfigureAwait(false);
            if (st == null) trackers.TryGetValue(response.SourceApplicationID, out st);
            return st;
        }
        private int tinymessages = 0;
        internal async Task<CommMessage> SendMessage(TcpClient client, CommMessage msg, ServerTracker st)
        {
            CommMessage response = null;
            await Util.mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                var netstream = client.GetStream();
                PrepareClientMessage(msg);
                var buffersize = client.SendBufferSize;

                Util.SerializeAndSendMessageBytes(msg, netstream, buffersize);

                byte[] firstbuffer = new byte[client.ReceiveBufferSize];
                netstream.ReadTimeout = ReadTimeout;
                //var bytesreceived = netstream.Read(firstbuffer, 0, client.ReceiveBufferSize);
                var bytesreceived = await netstream.ReadAsync(firstbuffer, 0, client.ReceiveBufferSize).ConfigureAwait(false);

                if (bytesreceived > 8)
                    response = Util.ReadMessageBytesUntilMessageComplete(firstbuffer, bytesreceived, netstream);
                else
                {
                    tinymessages++;
                    if (tinymessages < 10 || tinymessages % 25 == 0)
                        Util.Log($"WARNING (TcpClientComm.SendMessage): Tiny messages received: {tinymessages}");
                }

                if (response != null)
                {
                    if (response is ErrCommMessage)
                    {
                        throw new Exception("Remote machine returned an ErrCommMessage - see inner exception", (response as ErrCommMessage).CommMessageException);
                    }
                    else
                    {
                        if (st == null)
                        {
                            st = ServerTrackerGetOrAdd(response.SourceApplicationID, (IPEndPoint)client.Client.RemoteEndPoint,response.SourceMachine);
                            st.Client = client;
                            st.Parent = this;
                        }
                        else
                        {
                            ServerTrackerGetOrAdd(response.SourceApplicationID, st);
                        }
                        st.Process(response);
                        if (!(response is AckCommMessage)) ProcessResponseMethod?.Invoke(st, response);
                    }
                }

            }
            catch (Exception ex)
            {
                if (st != null) ServerTrackerRemove(st.ApplicationID);
                Util.Log(ex.ToString());
                throw ex;
            }
            finally
            {
                Util.mutex.Release();
            }
            return response;
        }
        private long SentMessageID = 0;
        private void PrepareClientMessage(CommMessage cm)
        {
            cm.MessageID = Interlocked.Increment(ref SentMessageID);
            cm.SourceApplicationID = ApplicationID;
            cm.SourceApplicationName = ApplicationName;
            cm.SourceMachine = Environment.MachineName;
            //cm.Offset = st == null ? TimeSpan.Zero : st.TimeOffset.Average;
        }
        public static TcpClientComm Default { get; set; }
    }
}
