using System;
using System.Collections.Generic;
using System.Text;

namespace MDDFoundation
{
    public class Schedule
    {
        public ScheduleType Type { get; set; }
        public DateTime LastRun { get; private set; }
        public DateTime Start { get; set; }
        public DateTime Stop { get; set; }
        public TimeSpan Increment { get; set; }

        public bool TimeToRun(DateTime asof = default)
        {
            if (asof == default) asof = DateTime.Now;
            switch (Type)
            {
                case ScheduleType.None:
                    return false;
                case ScheduleType.ByTimeSpan:
                    DateTime nextrun;
                    if (LastRun == default)
                    {
                        LastRun = new DateTime(asof.Year, asof.Month, asof.Day, Start.Hour, Start.Minute, Start.Second);
                        nextrun = LastRun;
                    }
                    else
                    {
                        nextrun = LastRun.Add(Increment);
                    }
                    if (nextrun.TimeOfDay > Stop.TimeOfDay) 
                    {
                        var tomorrow = LastRun.AddDays(1);
                        nextrun = new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, Start.Hour, Start.Minute, Start.Second);
                    }
                    if (asof >= nextrun)
                    {
                        while ((asof - LastRun) >= Increment) LastRun = LastRun.Add(Increment);
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }
    }
    public enum ScheduleType
    {
        None, ByTimeSpan
    }
}
