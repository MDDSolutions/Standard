using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MDDNetComm
{
    public enum ApplicationDiscoveryChangeType
    {
        Added,
        Updated,
        Removed
    }

    public sealed class ApplicationDiscoveryChangedEventArgs : EventArgs
    {
        public ApplicationDiscoveryChangedEventArgs(ApplicationDiscoveryChangeType changeType, DiscoveredApplication application)
        {
            ChangeType = changeType;
            Application = application;
        }

        public ApplicationDiscoveryChangeType ChangeType { get; }
        public DiscoveredApplication Application { get; }
    }

    public sealed class ApplicationDiscoveryOptions
    {
        public IPAddress MulticastAddress { get; set; } = IPAddress.Parse("239.255.42.99");
        public int MulticastPort { get; set; } = 51500;
        public TimeSpan AnnouncementInterval { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan ExpirationInterval { get; set; } = TimeSpan.FromSeconds(20);
    }

    public sealed class DiscoveredApplication
    {
        internal DiscoveredApplication(Guid applicationID, string applicationName, string machineName, IPEndPoint tcpEndPoint, DateTime lastSeenUtc)
        {
            ApplicationID = applicationID;
            ApplicationName = applicationName;
            MachineName = machineName;
            TcpEndPoint = tcpEndPoint;
            LastSeenUtc = lastSeenUtc;
        }

        public Guid ApplicationID { get; }
        public string ApplicationName { get; }
        public string MachineName { get; }
        public IPEndPoint TcpEndPoint { get; }
        public DateTime LastSeenUtc { get; }

        public override string ToString()
        {
            return $"{ApplicationName} on {MachineName} ({TcpEndPoint})";
        }
    }

    /// <summary>
    /// Opt-in multicast discovery for TCP applications and services.
    /// </summary>
    public sealed class ApplicationDiscovery : IDisposable
    {
        private const string ProtocolMarker = "MDDNETCOMM-DISCOVERY-1";
        private const string AnnounceAction = "A";
        private const string StopAction = "S";

        private readonly ConcurrentDictionary<Guid, DiscoveredApplication> applications = new ConcurrentDictionary<Guid, DiscoveredApplication>();
        private readonly ApplicationDiscoveryOptions options;
        private readonly object lifecycleLock = new object();

        private UdpClient receiver;
        private UdpClient publisher;
        private Timer timer;
        private bool started;

        public ApplicationDiscovery(string applicationName, Guid applicationID, int tcpListenerPort, ApplicationDiscoveryOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(applicationName))
                throw new ArgumentException("An application name is required.", nameof(applicationName));
            if (applicationID == Guid.Empty)
                throw new ArgumentException("A non-empty application ID is required.", nameof(applicationID));
            if (tcpListenerPort < 1 || tcpListenerPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(tcpListenerPort));

            ApplicationName = applicationName;
            ApplicationID = applicationID;
            TcpListenerPort = tcpListenerPort;
            this.options = options ?? new ApplicationDiscoveryOptions();

            if (this.options.MulticastAddress == null || !IsMulticastAddress(this.options.MulticastAddress))
                throw new ArgumentException("The discovery address must be an IPv4 multicast address.", nameof(options));
            if (this.options.MulticastPort < 1 || this.options.MulticastPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (this.options.AnnouncementInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (this.options.ExpirationInterval <= this.options.AnnouncementInterval)
                throw new ArgumentException("ExpirationInterval must be longer than AnnouncementInterval.", nameof(options));
        }

        public string ApplicationName { get; }
        public Guid ApplicationID { get; }
        public int TcpListenerPort { get; }
        public bool Started => started;

        public event EventHandler<ApplicationDiscoveryChangedEventArgs> ApplicationsChanged;
        public event EventHandler<Exception> Error;

        public IReadOnlyCollection<DiscoveredApplication> Applications =>
            applications.Values.OrderBy(x => x.ApplicationName).ThenBy(x => x.MachineName).ToList().AsReadOnly();

        public void Start()
        {
            lock (lifecycleLock)
            {
                if (started)
                    return;

                receiver = new UdpClient(AddressFamily.InterNetwork);
                receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver.ExclusiveAddressUse = false;
                receiver.Client.Bind(new IPEndPoint(IPAddress.Any, options.MulticastPort));
                receiver.JoinMulticastGroup(options.MulticastAddress);

                publisher = new UdpClient(AddressFamily.InterNetwork) { MulticastLoopback = true };
                started = true;
                BeginReceive();

                var period = ToTimerMilliseconds(options.AnnouncementInterval);
                timer = new Timer(TimerTick, null, 0, period);
            }
        }

        public void Stop()
        {
            lock (lifecycleLock)
            {
                if (!started)
                    return;

                TryPublish(StopAction);
                started = false;
                timer?.Dispose();
                timer = null;

                try
                {
                    receiver?.DropMulticastGroup(options.MulticastAddress);
                }
                catch (SocketException)
                {
                    // The socket may already have been closed by the receive callback.
                }

                receiver?.Close();
                receiver = null;
                publisher?.Close();
                publisher = null;
                applications.Clear();
            }
        }

        public DiscoveredApplication FindByApplicationName(string applicationName)
        {
            if (applicationName == null)
                return null;

            return applications.Values
                .Where(x => string.Equals(x.ApplicationName, applicationName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastSeenUtc)
                .FirstOrDefault();
        }

        public IReadOnlyCollection<DiscoveredApplication> FindAllByApplicationName(string applicationName)
        {
            if (applicationName == null)
                return new List<DiscoveredApplication>().AsReadOnly();

            return applications.Values
                .Where(x => string.Equals(x.ApplicationName, applicationName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.MachineName)
                .ToList()
                .AsReadOnly();
        }

        public void Announce()
        {
            EnsureStarted();
            TryPublish(AnnounceAction);
        }

        public void Dispose()
        {
            Stop();
        }

        private void TimerTick(object state)
        {
            if (!started)
                return;

            TryPublish(AnnounceAction);
            RemoveExpiredApplications();
        }

        private void BeginReceive()
        {
            try
            {
                receiver.BeginReceive(ReceiveCallback, receiver);
            }
            catch (ObjectDisposedException)
            {
                // Normal during shutdown.
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        private void ReceiveCallback(IAsyncResult result)
        {
            var udpClient = (UdpClient)result.AsyncState;
            try
            {
                var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                var bytes = udpClient.EndReceive(result, ref remoteEndPoint);
                ProcessDatagram(bytes, remoteEndPoint);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                OnError(ex);
            }

            if (started)
                BeginReceive();
        }

        private void ProcessDatagram(byte[] bytes, IPEndPoint remoteEndPoint)
        {
            var fields = Encoding.UTF8.GetString(bytes).Split('|');
            if (fields.Length != 7 || fields[0] != ProtocolMarker)
                return;
            if (!Guid.TryParse(fields[2], out Guid applicationID) || applicationID == ApplicationID)
                return;
            if (!int.TryParse(fields[3], out int tcpPort) || tcpPort < 1 || tcpPort > 65535)
                return;

            string applicationName;
            string machineName;
            try
            {
                applicationName = Decode(fields[5]);
                machineName = Decode(fields[6]);
            }
            catch (FormatException)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(applicationName) || string.IsNullOrWhiteSpace(machineName))
                return;

            if (fields[1] == StopAction)
            {
                if (applications.TryRemove(applicationID, out DiscoveredApplication removed))
                    OnApplicationsChanged(ApplicationDiscoveryChangeType.Removed, removed);
                return;
            }
            if (fields[1] != AnnounceAction)
                return;

            var discovered = new DiscoveredApplication(
                applicationID,
                applicationName,
                machineName,
                new IPEndPoint(remoteEndPoint.Address, tcpPort),
                DateTime.UtcNow);

            var changeType = applications.ContainsKey(applicationID)
                ? ApplicationDiscoveryChangeType.Updated
                : ApplicationDiscoveryChangeType.Added;
            applications.AddOrUpdate(applicationID, discovered, (key, existing) => discovered);
            OnApplicationsChanged(changeType, discovered);
        }

        private void RemoveExpiredApplications()
        {
            var cutoff = DateTime.UtcNow - options.ExpirationInterval;
            foreach (var application in applications.Values.Where(x => x.LastSeenUtc < cutoff).ToList())
            {
                if (applications.TryRemove(application.ApplicationID, out DiscoveredApplication removed))
                    OnApplicationsChanged(ApplicationDiscoveryChangeType.Removed, removed);
            }
        }

        private void TryPublish(string action)
        {
            try
            {
                var payload = string.Join(
                    "|",
                    ProtocolMarker,
                    action,
                    ApplicationID,
                    TcpListenerPort,
                    Convert.ToInt64(options.AnnouncementInterval.TotalMilliseconds),
                    Encode(ApplicationName),
                    Encode(Environment.MachineName));
                var bytes = Encoding.UTF8.GetBytes(payload);
                publisher.Send(bytes, bytes.Length, new IPEndPoint(options.MulticastAddress, options.MulticastPort));
            }
            catch (ObjectDisposedException)
            {
                // Normal during shutdown.
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        private void EnsureStarted()
        {
            if (!started)
                throw new InvalidOperationException("Application discovery has not been started.");
        }

        private void OnApplicationsChanged(ApplicationDiscoveryChangeType changeType, DiscoveredApplication application)
        {
            ApplicationsChanged?.Invoke(this, new ApplicationDiscoveryChangedEventArgs(changeType, application));
        }

        private void OnError(Exception exception)
        {
            Error?.Invoke(this, exception);
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static bool IsMulticastAddress(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] >= 224 && bytes[0] <= 239;
        }

        private static int ToTimerMilliseconds(TimeSpan interval)
        {
            return Convert.ToInt32(Math.Min(int.MaxValue, Math.Max(1, interval.TotalMilliseconds)));
        }
    }
}
