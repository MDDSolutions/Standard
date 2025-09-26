using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MDDDataAccess
{
    public sealed class QueryExecutionMetrics : IQueryExecutionMetrics
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _connectionTicks;
        private long _commandTicks;
        private long _readerOpenTicks;
        private long _hydrateTicks;
        private long _mapBuildTicks;
        private long _trackerProcessingTicks;
        private int _rows;

        public long OverallTime => Interlocked.Read(ref _connectionTicks);
        public long ConnectionTime => Interlocked.Read(ref _connectionTicks ) - Interlocked.Read(ref _commandTicks);
        public long CommandPreparationTime => Interlocked.Read(ref _commandTicks) - Interlocked.Read(ref _readerOpenTicks);
        public long ReaderOpenTime => Interlocked.Read(ref _readerOpenTicks) - Interlocked.Read(ref _hydrateTicks) - Interlocked.Read(ref _mapBuildTicks) - Interlocked.Read(ref _trackerProcessingTicks);
        public long HydrationTime => Interlocked.Read(ref _hydrateTicks);
        public long MapBuildTime => Interlocked.Read(ref _mapBuildTicks);
        public long TrackerProcessingTime => Interlocked.Read(ref _trackerProcessingTicks);
        public int Rows => Volatile.Read(ref _rows);


        public IDisposable MeasureConnection() => new Scope(this, MetricType.Connection);
        public IDisposable MeasureCommand() => new Scope(this, MetricType.Command);
        public IDisposable MeasureReaderOpen() => new Scope(this, MetricType.ReaderOpen);
        public IDisposable MeasureHydration() => new Scope(this, MetricType.Hydration);
        public IDisposable MeasureMapBuildTime() => new Scope(this, MetricType.MapBuild);
        public IDisposable MeasureTrackerProcessingTime() => new Scope(this, MetricType.TrackerProcessing);
        public void IncrementRowCount() => Interlocked.Increment(ref _rows);

        private enum MetricType { Connection, Command, ReaderOpen, Hydration, MapBuild, TrackerProcessing }

        private struct Scope : IDisposable
        {
            private readonly QueryExecutionMetrics _owner;
            private readonly MetricType _type;
            private readonly long _start;

            public Scope(QueryExecutionMetrics owner, MetricType type)
            {
                _owner = owner;
                _type = type;
                _start = owner._stopwatch.ElapsedTicks;
            }

            public void Dispose()
            {
                if (_owner == null) return;
                var elapsed = _owner._stopwatch.ElapsedTicks - _start;
                switch (_type)
                {
                    case MetricType.Connection:
                        Interlocked.Add(ref _owner._connectionTicks, elapsed);
                        break;
                    case MetricType.Command:
                        Interlocked.Add(ref _owner._commandTicks, elapsed);
                        break;
                    case MetricType.ReaderOpen:
                        Interlocked.Add(ref _owner._readerOpenTicks, elapsed);
                        break;
                    case MetricType.Hydration:
                        Interlocked.Add(ref _owner._hydrateTicks, elapsed);
                        break;
                    case MetricType.MapBuild:
                        Interlocked.Add(ref _owner._mapBuildTicks, elapsed);
                        break;
                    case MetricType.TrackerProcessing:
                        Interlocked.Add(ref _owner._trackerProcessingTicks, elapsed);
                        break;
                }
            }
        }
    }
    public sealed class NoopQueryExecutionMetrics : IQueryExecutionMetrics
    {
        public static readonly NoopQueryExecutionMetrics Instance = new NoopQueryExecutionMetrics();
        private static readonly IDisposable _noopDisposable = new NoopDisposable();

        public long OverallTime => 0;
        public long ConnectionTime => 0;
        public long CommandPreparationTime => 0;
        public long ReaderOpenTime => 0;
        public long HydrationTime => 0;
        public long TrackerProcessingTime => 0;
        public long MapBuildTime => throw new NotImplementedException();
        public int Rows => 0;

        public IDisposable MeasureConnection() => _noopDisposable;
        public IDisposable MeasureCommand() => _noopDisposable;
        public IDisposable MeasureReaderOpen() => _noopDisposable;
        public IDisposable MeasureHydration() => _noopDisposable;
        public IDisposable MeasureMapBuildTime() => _noopDisposable;
        public IDisposable MeasureTrackerProcessingTime() => _noopDisposable;
        public void IncrementRowCount() { }
   
        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
    public interface IQueryExecutionMetrics
    {
        long OverallTime { get; }
        long ConnectionTime { get; }
        long CommandPreparationTime { get; }
        long ReaderOpenTime { get; }
        long HydrationTime { get; }
        long MapBuildTime { get; }
        long TrackerProcessingTime { get; }
        int Rows { get; }

        IDisposable MeasureConnection();
        IDisposable MeasureCommand();
        IDisposable MeasureReaderOpen();
        IDisposable MeasureHydration();
        IDisposable MeasureMapBuildTime();
        IDisposable MeasureTrackerProcessingTime();
        void IncrementRowCount();
    }
    public class CommandExecutionLog
    {
        public DateTime ExecDateTime { get; set; }
        public string ExecCommand { get; set; }
        public float ConnectionTime { get; set; }
        public float CommandTime { get; set; }
        public float ReaderTime { get; set; }
        public float HydrationTime { get; set; }
        public float MapBuildTime { get; set; }
        public float TrackerTime { get; set; }
    }
}