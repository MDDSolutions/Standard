using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MDDDataAccess
{
    public class OperationProgress
    {
        public string OperationDuring { get; set; } = "<cmd>";
        public string OperationComplete { get; set; } = "<cmd>";
        public Stopwatch Stopwatch { get; set; } = null;
        public bool ReportElapsed { get; set; } = false;
        public string SpecialStatus { get; set; } = null;

        public double FinalStatus { get; set; } = 100;

        private double currentstatus = 0;
        public double CurrentStatus
        {
            get { return currentstatus; }
            set 
            {
                if (Stopwatch == null && currentstatus == 0 && value > 0)
                    Stopwatch = Stopwatch.StartNew();
                if (currentstatus < FinalStatus && value >= FinalStatus)
                    Stopwatch.Stop();
                currentstatus = value; 
            }
        }
        public override string ToString()
        {
            if (SpecialStatus != null)
                return SpecialStatus;
            if (currentstatus >= FinalStatus)
                return $"{OperationComplete} complete in {Stopwatch.Elapsed}";
            else if (ReportElapsed)
                return $"{OperationDuring} running for {Stopwatch.Elapsed}";   
            else if (currentstatus > 0)
                return $"{OperationDuring} {currentstatus / FinalStatus * 100:N1}% Elapsed/Remaining: {Stopwatch.Elapsed}/{EstimatedRemaining}";
            else
                return $"{OperationComplete} not started or no progress yet";
        }
        public TimeSpan EstimatedRemaining { get => TimeSpan.FromMilliseconds((Stopwatch.ElapsedMilliseconds * FinalStatus / currentstatus) - Stopwatch.ElapsedMilliseconds); }
        

        public static OperationProgress StartNew()
        {
            OperationProgress progress = new OperationProgress();
            progress.Stopwatch = Stopwatch.StartNew();
            return progress;
        }
    }
}
