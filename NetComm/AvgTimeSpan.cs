using System;
using System.Linq;

namespace MDDNetComm
{
    public class AvgTimeSpan
    {
        private TimeSpanSample[] data = new TimeSpanSample[10];
        public void AddSample(TimeSpan timespan)
        {
            DateTime mintime = DateTime.MaxValue;
            int index = 0;
            for (int i = 0; i < 10; i++)
            {
                if (data[i] == null)
                {
                    index = i;
                    break;
                }
                else
                {
                    if (data[i].SampleTime < mintime)
                    {
                        mintime = data[i].SampleTime;
                        index = i;
                    }
                }
            }
            data[index] = new TimeSpanSample { Sample = timespan, SampleTime = DateTime.Now };
        }
        public TimeSpan Average
        {
            get
            {
                try
                {
                    return TimeSpan.FromMilliseconds(data.Where(x => x != null).Average(x => x.Sample.TotalMilliseconds));
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message != "Sequence contains no elements")
                        throw ex;
                }
                return TimeSpan.Zero;
            }
        }
    }
    public class TimeSpanSample
    {
        public override string ToString()
        {
            return $"Time: {SampleTime.ToString("hh:mm:ss.ff")} Value: {Sample}";
        }
        public TimeSpan Sample { get; set; }
        public DateTime SampleTime { get; set; }
    }
}
