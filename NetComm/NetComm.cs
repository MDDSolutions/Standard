using System;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;

namespace MDDNetComm
{
    public static class Util
    {
        internal static void SerializeAndSendMessageBytes(CommMessage msg, NetworkStream netstream, int buffersize)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                msg.MessageSentTime = DateTime.Now;
                bf.Serialize(stream, msg);
                long msglength = stream.Length + 8;
                byte[] msgbytes = new byte[msglength];
                Buffer.BlockCopy(BitConverter.GetBytes(stream.Length), 0, msgbytes, 0, 8);
                Buffer.BlockCopy(stream.ToArray(), 0, msgbytes, 8, (int)stream.Length);

                int position = 0;
                long size;
                int curpacket = 0;
                int numpackets = Convert.ToInt32(Math.Ceiling(msglength / Convert.ToDouble(buffersize)));
                //Log($"sending message - size is {msglength}, buffersize is {buffersize} so will need {numpackets} packets");
                while (position < msglength)
                {
                    if (msglength - position > buffersize)
                        size = buffersize;
                    else
                        size = msglength - position;
                    curpacket++;
                    //DisplayMessage?.Invoke(this, $"sending packet {curpacket}/{numpackets} - [{msgbytes[position]},{msgbytes[position + 1]},{msgbytes[position + 2]}] ... [{msgbytes[position + size - 3]},{msgbytes[position + size - 2]},{msgbytes[position + size - 1]}]");
                    netstream.Write(msgbytes, position, (int)size);
                    position = position + (int)size;
                }
            }
        }
        internal static CommMessage ReadMessageBytesUntilMessageComplete(byte[] FirstArray, int bytesreceived, NetworkStream netstream)
        {
            DateTime ReceiveTime = DateTime.Now;
            var size = BitConverter.ToInt64(FirstArray, 0);
            var final = new byte[size];
            var position = bytesreceived - 8;
            Buffer.BlockCopy(FirstArray, 8, final, 0, position);
            var count = 1;
            var zerobytecalls = 0;
            while (position < size)
            {
                count++;
                bytesreceived = netstream.Read(FirstArray, 0, FirstArray.Length);
                if (bytesreceived == 0) zerobytecalls++;
                if (zerobytecalls > 100) throw new Exception("Util.ReadMessageBytesUntilMessageComplete: Stuck in loop not receiving any bytes");
                Buffer.BlockCopy(FirstArray, 0, final, position, bytesreceived);
                position += bytesreceived;
            }
            //Log($"message received - size was {size} and required {count} calls to Read");
            CommMessage cm;
            using (MemoryStream s = new MemoryStream(final))
            {
                try
                {
                    cm = (CommMessage)bf.Deserialize(s);
                    cm.MessageReceiveTime = ReceiveTime;
                }
                catch (Exception ex)
                {
                    cm = new ErrCommMessage { CommMessageException = ex };
                    Log(ex.ToString());
                }
            }
            return cm;
        }
        private static BinaryFormatter bf = new BinaryFormatter();
        internal static SemaphoreSlim mutex = new SemaphoreSlim(1, 1);
        internal static void Log(string LogStr, bool Initialize = false)
        {
            DirectoryInfo logfiledir = (new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location)).Directory;
            string CurLogFile = Path.Combine(logfiledir.FullName, "NetComm_log.txt");
            bool Finished = false;
            bool WriteHeader = !File.Exists(CurLogFile) || LogStr == "";
            if (!WriteHeader && Initialize)
            {
                File.Delete(CurLogFile);
                WriteHeader = true;
            }
            while (!Finished)
            {
                try
                {
                    using (StreamWriter writer = File.AppendText(CurLogFile))
                    {
                        if (WriteHeader)
                            writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF") + " -- NetComm Log File");
                        if (LogStr != "")
                            writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF") + " -- " + LogStr);
                    }
                    Finished = true;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("because it is being used by another process"))
                        Thread.Sleep(50);
                    else
                        throw ex;
                }

            }
        }
        //public static void SynchronizedInvoke(this ISynchronizeInvoke sync, Action action)
        //{
        //    if (!sync.InvokeRequired)
        //    {
        //        action();
        //        return;
        //    }
        //    sync.Invoke(action, new object[] { });
        //}
        public static DateTime BuildTime()
        {
            Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            DateTime buildtime = new DateTime(2000, 1, 1).AddDays(version.Build).AddSeconds(version.Revision * 2);
            if (buildtime.IsDaylightSavingTime())
                buildtime = buildtime.AddHours(1);
            return buildtime;
        }
    }
    public enum ChangeType
    {
        Add, Remove, Update
    }
}
