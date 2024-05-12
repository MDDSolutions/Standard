using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace MDDDataAccess
{
    public class CommandStat
    {
        public string CommandText { get; set; }
        public int ExecutionCount { get; set; }
        public TimeSpan CumulativeExecution { get; set; }
        public TimeSpan LongestExecution { get; set; }
        public TimeSpan ShortestExecution { get; set; }
        public DateTime FirstExecution { get; set; }
        public DateTime LastExecution { get; set; }
        public override string ToString()
        {
            return $"Count: {ExecutionCount:D4}, Elapsed: {CumulativeExecution}, Longest: {LongestExecution}, Shortest: {ShortestExecution} First: {FirstExecution}, Last: {LastExecution}, Command: {CommandText}";
        }


        private static ConcurrentDictionary<string, CommandStat> stats = new ConcurrentDictionary<string, CommandStat>();

        public static void RecordStat(string text, TimeSpan elapsed)
        {
            var now = DateTime.Now;

            stats.AddOrUpdate(
                text,
                new CommandStat
                {
                    CommandText = text,
                    ExecutionCount = 1,
                    CumulativeExecution = elapsed,
                    LongestExecution = elapsed,
                    ShortestExecution = elapsed,
                    FirstExecution = now,
                    LastExecution = now
                },
                (txt, cs) =>
                {
                    lock (cs)
                    {
                        cs.ExecutionCount += 1;
                        cs.CumulativeExecution += elapsed;
                        if (elapsed > cs.LongestExecution) cs.LongestExecution = elapsed;
                        if (elapsed < cs.ShortestExecution) cs.ShortestExecution = elapsed;
                        cs.LastExecution = now;
                    }
                    return cs;
                }
                );
        }
        public static List<CommandStat> List()
        {
            var l = new List<CommandStat>();
            foreach (var item in stats.Values)
                l.Add(item);
            return l;
        }
        public static string Report()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var item in stats.Values)
            {
                sb.AppendLine(item.ToString());
            }
            return sb.ToString();
        }
    }
}
