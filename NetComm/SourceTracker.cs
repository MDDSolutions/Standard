using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MDDNetComm
{
    public abstract class SourceTracker
    {
        public Guid ApplicationID { get; set; }
        public string MachineName { get; set; }
        public IPEndPoint IPEndPoint { get; set; }
        public long LastMessageID { get; set; }
        public TcpClient Client { get; set; }
        public AvgTimeSpan TimeOffset { get; set; } = new AvgTimeSpan();
        //public TimeSpan Latency { get; set; } = TimeSpan.Zero;
        public DateTime Clock
        {
            get
            {
                return (DateTime.Now - TimeOffset.Average).AddMilliseconds(7);// + Latency;
            }
        }
        public void Process(CommMessage cm)
        {
            LastMessageID = cm.MessageID;
            TimeOffset.AddSample(cm.MessageReceiveTime - cm.MessageSentTime);

            //var local = Math.Abs(TimeOffset.Average.TotalMilliseconds);
            //var remote = Math.Abs(cm.Offset.TotalMilliseconds);

            //var diff = Math.Abs(local - remote) / 2.0;

            //Latency = TimeSpan.FromMilliseconds(diff);

            if (ApplicationID != cm.SourceApplicationID)
            {
                if (ApplicationID == Guid.Empty)
                    ApplicationID = cm.SourceApplicationID;
                else
                {
                    if (cm.SourceApplicationID == Guid.Empty)
                        throw new Exception("SourceTracker.Process: Sending process sent an empty Application ID - looks like it did not prepare the message");
                    else
                        throw new Exception("SourceTracker.Process: Wrong Application ID - this should never really happen");
                }

            }
            //TODO:  stats, lag time...
        }
        public override string ToString()
        {
            if (IPEndPoint.AddressFamily == AddressFamily.InterNetwork)
                return $"{MachineName}({IPEndPoint.Address}):{IPEndPoint.Port} - {ApplicationID}";
            else
                return $"{MachineName}:{IPEndPoint.Port} - {ApplicationID}";
        }
        public bool Connected { get; set; }
    }
    public class ClientTracker : SourceTracker
    {
        public ClientTracker(Guid applicationid, IPEndPoint ipendpoint, string machinename)
        {
            ApplicationID = applicationid;
            IPEndPoint = ipendpoint;
            MachineName = machinename;
        }
        public byte[] ReceivePacketBuffer { get; set; }
        public TcpServer Parent { get; set; }
        private async Task<CommMessage> ProcessMessage(CommMessage cm)
        {
            CommMessage resp = null;

            if (cm != null)
            {
                if (cm is ErrCommMessage)
                {
                    resp = cm;
                }
                else
                {
                    if (ApplicationID == Guid.Empty)
                    {
                        ApplicationID = cm.SourceApplicationID;
                        MachineName = cm.SourceMachine;
                        Parent.ClientTrackerGetOrAdd(ApplicationID, this);
                    }
                    Process(cm);
                    //TODO - maybe - if (cm is ShutdownCommMessage) ...

                    if (Parent.ProcessMessageMethod != null)
                    {
                        resp = await Parent.ProcessMessageMethod(this, cm).ConfigureAwait(false) ?? new AckCommMessage(cm);
                    }

                    if (resp == null) resp = new ErrCommMessage();
                }
            }
            if (resp != null) PrepareServerMessage(resp);
            return resp;
        }
        private long SentMessageID = 0;
        internal void PrepareServerMessage(CommMessage cm)
        {
            cm.MessageID = Interlocked.Increment(ref SentMessageID);
            cm.SourceApplicationID = Parent.ApplicationID;
            cm.SourceApplicationName = Parent.ApplicationName;
            cm.SourceMachine = Environment.MachineName;
            //cm.Offset = TimeOffset.Average;
        }
        private int tinymessages = 0;
        public async void ReadCallback(IAsyncResult ar)
        {
            var netstream = (NetworkStream)ar.AsyncState;
            CommMessage resp = null;
            bool sendresponse = true;
            try
            {
                var bytesreceived = netstream.EndRead(ar);
                if (bytesreceived > 8)
                {
                    var msg = Util.ReadMessageBytesUntilMessageComplete(ReceivePacketBuffer, bytesreceived, netstream);

                    resp = await ProcessMessage(msg).ConfigureAwait(false);
                }
                else
                {
                    tinymessages++;
                    if (tinymessages < 10 || tinymessages % 25 == 0)
                        Util.Log($"WARNING (ClientTracker.ReadCallback): Tiny messages received: {tinymessages}");
                }
            }
            catch (IOException)
            {
                sendresponse = false;
                Parent.ClientTrackerClientDisconnected(this);
            }
            catch (Exception ex)
            {
                Parent.ClientTrackerError(this, ex);
                Util.Log(ex.ToString());
            }
            finally
            {
                if (sendresponse)
                {
                    if (resp != null)
                    {
                        Util.SerializeAndSendMessageBytes(resp, netstream, Client.SendBufferSize);
                    }
                    if (Client.ReceiveBufferSize != ReceivePacketBuffer.Length)
                    {
                        Util.Log($"Client ReceiveBuffer resize - {ReceivePacketBuffer.Length} -> {Client.ReceiveBufferSize}");
                        ReceivePacketBuffer = new byte[Client.ReceiveBufferSize];
                    }
                    netstream.BeginRead(ReceivePacketBuffer, 0, ReceivePacketBuffer.Length, ReadCallback, netstream);
                }
            }
        }
    }
    public class ServerTracker : SourceTracker
    {
        public ServerTracker(Guid applicationid, IPEndPoint ipendpoint, string machinename)
        {
            ApplicationID = applicationid;
            IPEndPoint = ipendpoint;
            MachineName = machinename;
        }
        public TcpClientComm Parent { get; set; }
        public async Task<CommMessage> SendMessage(CommMessage msg)
        {
            return await Parent.SendMessage(Client, msg, this).ConfigureAwait(false);
        }
    }
}
