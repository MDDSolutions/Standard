using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace MDDNetComm
{
    //public delegate CommMessage ProcessMessageDelegate(ClientTracker tracker, CommMessage cm);

    public class TcpServer
    {
        public event EventHandler<ChangeType> ConnectionsChanged;
        public TcpServer(string inApplicationName, int inTcpListenerPort = 0)
        {
            ApplicationName = inApplicationName;
            Util.Log($"Listener starting - Application Name: {ApplicationName} ID: {ApplicationID}", true);

            tcpListener = new TcpListener(IPAddress.Any, inTcpListenerPort);
            //tcpListener.Server.DualMode = true;
            tcpListener.Start();
            tcpListener.BeginAcceptTcpClient(TcpServerCallback, tcpListener);
            var ep = (IPEndPoint)tcpListener.LocalEndpoint;
            TcpListenerPort = ep.Port;
        }
        public string ApplicationName { get; set; }
        public Guid ApplicationID { get; set; } = Guid.NewGuid();
        public int TcpListenerPort { get; set; }
        public int ReadTimeout { get; set; } = 500;
        public Func<ClientTracker, CommMessage, Task<CommMessage>> ProcessMessageMethod; // { get; set; }
        private ConcurrentDictionary<Guid, ClientTracker> trackers { get; set; } = new ConcurrentDictionary<Guid, ClientTracker>();
        public ClientTracker ClientTrackerGetOrAdd(Guid key, ClientTracker value)
        {
            value.Connected = true;
            bool added = false;
            var retval = trackers.GetOrAdd(key, (k) => { added = true; return value; });
            if (added)
            {
                Util.Log($"Connected to client {retval}");
                ConnectionsChanged?.Invoke(retval, ChangeType.Add);
            }
            return retval;
        }
        public ClientTracker ServerTrackerGetOrAdd(Guid key, IPEndPoint ep, string SourceMachine)
        {
            bool added = false;
            var st = trackers.GetOrAdd(key, (k) => { added = true; return new ClientTracker(key, ep, SourceMachine); });
            st.Connected = true;
            if (added)
            {
                Util.Log($"Connected to client: {st}");
                ConnectionsChanged?.Invoke(st, ChangeType.Add);
            }
            return st;
        }
        public bool ClientTrackerRemove(Guid key)
        {
            if (!(trackers.TryGetValue(key, out ClientTracker st)))
                return false;
            st.Connected = false;
            Util.Log($"Disconnected from server: {st}");
            ConnectionsChanged?.Invoke(st, ChangeType.Remove);
            return true;
        }
        public IEnumerable<ClientTracker> ClientTrackers()
        {
            return trackers.Values.Where(x => x.Connected);
        }
        //public event EventHandler<ClientTracker> ClientConnected;
        public event EventHandler<Exception> Error;
        internal void ClientTrackerError(ClientTracker st, Exception ex)
        {
            Error?.Invoke(st, ex);
        }
        public event EventHandler<ClientTracker> ClientDisconnected;
        internal void ClientTrackerClientDisconnected(ClientTracker st)
        {
            ClientTrackerRemove(st.ApplicationID);
            Util.Log($"ClientDisconnected: {st}");
            ClientDisconnected?.Invoke(this, st);
        }
        public event EventHandler<string> DisplayMessage;
        internal void ClientTrackerDisplayMessage(ClientTracker st, string msg)
        {
            DisplayMessage?.Invoke(st, msg);
        }
        private TcpListener tcpListener = null;
        private void TcpServerCallback(IAsyncResult ar)
        {
            bool RunAgain = true;
            TcpClient client = tcpListener.EndAcceptTcpClient(ar);
            var netstream = client.GetStream();
            ClientTracker st = new ClientTracker(Guid.Empty, (IPEndPoint)client.Client.RemoteEndPoint, null);
            st.Client = client;
            st.ReceivePacketBuffer = new byte[client.ReceiveBufferSize];
            st.Parent = this;
            netstream.BeginRead(st.ReceivePacketBuffer, 0, client.ReceiveBufferSize, st.ReadCallback, netstream);
            if (RunAgain)
                try
                {
                    tcpListener.BeginAcceptTcpClient(TcpServerCallback, tcpListener);
                }
                catch (Exception ex)
                {
                    Util.Log(ex.ToString());
                }
        }
        public static TcpServer Default { get; set; }
        public List<string> IPAddressInfo()
        {
            var l = new List<string>();
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if ((item.NetworkInterfaceType == NetworkInterfaceType.Ethernet || item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) && item.OperationalStatus == OperationalStatus.Up)
                {
                    foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            l.Add($"{item.NetworkInterfaceType} - {item.Description} - {ip.Address}:{TcpListenerPort}");
                        }
                    }
                }
            }
            return l;
        }
    }
}
