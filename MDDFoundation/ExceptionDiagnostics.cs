using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public static class ExceptionDiagnostics
    {
        private static readonly object LockObject = new object();
        private static readonly HashSet<string> LogKeys = new HashSet<string>();
        private static bool installed;

        public static void Install(Action<string> log, string source = null, bool observeUnobservedTaskExceptions = true)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            lock (LockObject)
            {
                if (installed)
                    return;
                installed = true;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                SafeLog(log, source, $"AppDomain.UnhandledException terminating={e.IsTerminating}: {e.ExceptionObject}");

            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                if (!IsAssemblyOrTypeLoadException(e.Exception))
                    return;

                SafeLogOnce(log, source, $"FirstChance:{e.Exception.GetType().FullName}:{e.Exception.Message}", $"FirstChance assembly/type load exception: {e.Exception}");
            };

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var requester = e.RequestingAssembly == null ? "<unknown>" : e.RequestingAssembly.FullName;
                SafeLogOnce(log, source, $"AssemblyResolve:{e.Name}:{requester}", $"Assembly resolve failed: requested='{e.Name}', requester='{requester}'");
                return null;
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                SafeLog(log, source, $"TaskScheduler.UnobservedTaskException: {e.Exception}");
                if (observeUnobservedTaskExceptions)
                    e.SetObserved();
            };
        }

        public static void LogThreadException(Action<string> log, Exception exception, string source = null)
        {
            SafeLog(log, source, $"Application.ThreadException: {exception}");
        }

        private static bool IsAssemblyOrTypeLoadException(Exception ex)
        {
            return ex is FileNotFoundException ||
                   ex is FileLoadException ||
                   ex is BadImageFormatException ||
                   ex is TypeLoadException ||
                   ex is MissingMethodException;
        }

        private static void SafeLog(Action<string> log, string source, string message)
        {
            try
            {
                log(string.IsNullOrWhiteSpace(source) ? message : $"{source}: {message}");
            }
            catch
            {
            }
        }

        private static void SafeLogOnce(Action<string> log, string source, string key, string message)
        {
            lock (LockObject)
            {
                if (!LogKeys.Add(key))
                    return;
            }

            SafeLog(log, source, message);
        }
    }
}
