using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MDDDataAccess
{
    public class CommandStat
    {
        public string CommandText { get; set; }
        public int ExecutionCount { get; set; }
        public int CumulativeExecution { get; set; }
        public int LongestExecution { get; set; }
        public int ShortestExecution { get; set; }
        public DateTime FirstExecution { get; set; }
        public DateTime LastExecution { get; set; }
        public override string ToString()
        {
            return $"Count: {ExecutionCount:D4}, Elapsed: {CumulativeExecution}, Longest: {LongestExecution}, Shortest: {ShortestExecution} First: {FirstExecution}, Last: {LastExecution}, Command: {CommandText}";
        }

        private static readonly ConcurrentDictionary<string, CommandStat> stats = new ConcurrentDictionary<string, CommandStat>();
        private static readonly ConcurrentDictionary<string, object> locks = new ConcurrentDictionary<string, object>();

        public static void RecordStat(string text, int elapsed)
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
                    var lockObject = locks.GetOrAdd(txt, _ => new object());
                    lock (lockObject)
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
            return stats.Values.ToList();
        }
        public static string Report()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var item in stats.Values.OrderByDescending(x => x.CumulativeExecution))
            {
                sb.AppendLine(item.ToString());
            }
            return sb.ToString();
        }
    }
}
