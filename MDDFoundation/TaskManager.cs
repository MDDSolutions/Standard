using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace MDDFoundation
{
    //Beginnings of a base class for managing queues with concurrent execution - specific functionality is in LogShipping's CombineManager
    //functionality needs to be generalized and migrated here
    public class TaskManager
    {
        public event EventHandler<Tuple<string, int>> StatusUpdateEvent;
        public event EventHandler<TaskWrapper> ProgressUpdateEvent;
        public event EventHandler<int[]> TaskListChangeEvent;
        public void FireStatusUpdateEvent(Tuple<string, int> msgseverity) => StatusUpdateEvent?.Invoke(this, msgseverity);
        public void FireProgressUpdateEvent(TaskWrapper taskwrapper) => ProgressUpdateEvent?.Invoke(this, taskwrapper);
        public void FireTaskListChangeEvent(int[] channels) => TaskListChangeEvent?.Invoke(this, channels);
    }
    public class QueueTask
    {
        public byte NumRetries { get; set; }
        public FileCopyProgress FileCopyProgress { get; set; }
        public int QueuePriority { get; set; }

    }
    public class TaskWrapper
    {
        public Task Task { get; set; }
        public FileCopyProgress FileCopyProgress { get; set; }
        public DateTime LastUpdate { get; set; }
        public DateTime TaskStartTime { get; set; }
        public int Channel { get; set; }
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("TaskWrapper: ");
            if (Task != null)
            {
                sb.Append($", {Task.Status}");
            }
            else
            {
                sb.Append(", <null>");
            }
            if (FileCopyProgress != null)
                sb.Append($", {FileCopyProgress}");
            return sb.ToString();
        }
    }
}
