using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public delegate Task BasicProgressDelegate(CancellationToken token, IProgress<BasicProgress> progress);
    public class BasicProgress
    {
        public int CurrentCount { get; set; }
        public int TotalCount { get; set; }
        public string Message { get; set; }
        public override string ToString()
        {
            return $"[{CurrentCount}/{TotalCount}] {Message}";
        }
        public async static Task TestBasicProgress(CancellationToken token, IProgress<BasicProgress> progress)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (progress != null)
                {
                    var args = new BasicProgress { CurrentCount = i + 1, TotalCount = 10, Message = $"Test {i * 100}" };
                    progress.Report(args);
                }
                if (token.IsCancellationRequested) break;
            }
        }
    }
}
