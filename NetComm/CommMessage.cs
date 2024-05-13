using System;

namespace MDDNetComm
{
    [Serializable]
    public class CommMessage
    {
        public long MessageID { get; set; }
        public DateTime MessageSentTime { get; set; }
        public DateTime MessageReceiveTime { get; set; }
        public Guid SourceApplicationID { get; set; }
        public string SourceApplicationName { get; set; }
        public string SourceMachine { get; set; }
        //public TimeSpan Offset { get; set; }
        public Guid? TargetID { get; set; }
        public override string ToString()
        {
            if (MessageReceiveTime != DateTime.MinValue)
                return $"Received: {MessageReceiveTime:HH:mm:ss.FFF} Lag: {(MessageReceiveTime - MessageSentTime).TotalMilliseconds} {ToStringBasic()}";
            if (MessageSentTime != DateTime.MinValue)
                return $"Sent: {MessageSentTime:HH:mm:ss.FFF} {ToStringBasic()}";
            return ToStringBasic();
        }
        public virtual string ToStringBasic()
        {
            return $"ID: {MessageID}  Type: {GetType().Name} SourceID: {SourceApplicationID}";
        }
    }
    [Serializable]
    public class AckCommMessage : CommMessage
    {
        public long AckMessageID { get; set; }
        public AckCommMessage(CommMessage MessageToAcknowledge)
        {
            AckMessageID = MessageToAcknowledge.MessageID;
        }
    }
    [Serializable]
    public class ErrCommMessage : CommMessage
    {
        public Exception CommMessageException { get; set; }
    }
}
