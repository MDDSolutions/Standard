using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MDDDataAccess
{
    public sealed class QueryExecutionMetrics
    {
        private readonly Stopwatch _stopwatch;
        private readonly bool _enabled;
        private long _connectionTicks;
        private long _commandTicks;
        private long _readerOpenTicks;
        private long _readerReadTicks;
        private long _rowTicks;
        private long _mapBuildTicks;
        private long _trackerProcessingTicks;
        private int _rows;

        public static QueryExecutionMetrics Disabled { get; } = new QueryExecutionMetrics(false);

        public QueryExecutionMetrics()
            : this(true)
        {
        }

        private QueryExecutionMetrics(bool enabled)
        {
            _enabled = enabled;
            if (enabled)
            {
                _stopwatch = Stopwatch.StartNew();
            }
        }

        public long OverallTime => _enabled ? Interlocked.Read(ref _connectionTicks) : 0;
        public long ConnectionTime => _enabled ? Interlocked.Read(ref _connectionTicks) - Interlocked.Read(ref _commandTicks) : 0;
        public long CommandPreparationTime => _enabled ? Interlocked.Read(ref _commandTicks) - Interlocked.Read(ref _readerOpenTicks) : 0;
        public long ReaderOpenTime => _enabled ? Interlocked.Read(ref _readerOpenTicks) - Interlocked.Read(ref _readerReadTicks) : 0;
        public long ReaderReadTime => _enabled ? Interlocked.Read(ref _readerReadTicks) - Interlocked.Read(ref _rowTicks) : 0;
        public long RowTime => _enabled ? Interlocked.Read(ref _rowTicks) - Interlocked.Read(ref _mapBuildTicks) - Interlocked.Read(ref _trackerProcessingTicks) : 0;
        public long MapBuildTime => _enabled ? Interlocked.Read(ref _mapBuildTicks) : 0;
        public long TrackerProcessingTime => _enabled ? Interlocked.Read(ref _trackerProcessingTicks) : 0;

        public int Rows => _enabled ? Volatile.Read(ref _rows) : 0;


        public Scope MeasureConnection() => _enabled ? new Scope(this, MetricType.Connection) : default;
        public Scope MeasureCommand() => _enabled ? new Scope(this, MetricType.Command) : default;
        public Scope MeasureReaderOpen() => _enabled ? new Scope(this, MetricType.ReaderOpen) : default;
        public Scope MeasureReaderRead() => _enabled ? new Scope(this, MetricType.ReaderRead) : default;
        public Scope MeasureRowTime() => _enabled ? new Scope(this, MetricType.InRow) : default;
        public Scope MeasureMapBuildTime() => _enabled ? new Scope(this, MetricType.MapBuild) : default;
        public Scope MeasureTrackerProcessingTime() => _enabled ? new Scope(this, MetricType.TrackerProcessing) : default;

        public void IncrementRowCount()
        {
            if (_enabled)
            {
                Interlocked.Increment(ref _rows);
            }
        }

        public enum MetricType { Connection, Command, ReaderOpen, ReaderRead, InRow, MapBuild, TrackerProcessing }

        public readonly struct Scope : IDisposable
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
                    case MetricType.ReaderRead:
                        Interlocked.Add(ref _owner._readerReadTicks, elapsed);
                        break;
                    case MetricType.InRow:
                        Interlocked.Add(ref _owner._rowTicks, elapsed);
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
    public class CommandExecutionLog
    {
        public DateTime ExecDateTime { get; set; }
        public string ObjectType { get; set; }
        public string ExecCommand { get; set; }
        public int QueryRowCount { get; set; }
        public float ConnectionTime { get; set; }
        public float CommandTime { get; set; }
        public float ReaderOpenTime { get; set; }
        public float ReaderReadTime { get; set; }
        public float RowTime { get; set; }
        public float MapBuildTime { get; set; }
        public float TrackerTime { get; set; }
    }
}