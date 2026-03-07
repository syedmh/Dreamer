using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleApp2
{
    internal class Program
    {
        static void Main(string[] args)
        {
            LogFinder();
        }

        private static void LogFinder()
        {
            List<Tuple<string, DateTime?, bool>> rawLogDataCollection = new List<Tuple<string, DateTime?, bool>>();
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test3", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test4", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test5", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test3", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test4", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test6", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test2", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test7", DateTime.Now.AddMinutes(1), false));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test6", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test7", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test8", DateTime.Now, true));
            rawLogDataCollection.Add(new Tuple<string, DateTime?, bool>("test9", DateTime.Now, false));

            List<LogData> cleanLogData = new List<LogData>();

            foreach (var rawLogData in rawLogDataCollection)
            {
                var cleanLogs = cleanLogData.Where(x => x.Name == rawLogData.Item1).Where(x => !x.IsComplete);
                var logCount = cleanLogs.Count();

                if (cleanLogs != null && logCount == 0)
                {
                    if (rawLogData.Item3)
                    {
                        cleanLogData.Add(new LogData { Name = rawLogData.Item1, Start = rawLogData.Item2, End = null, IsComplete = false });
                    }
                    else
                    {
                        cleanLogData.Add(new LogData { Name = rawLogData.Item1, Start = null, End = rawLogData.Item2, IsComplete = false });
                    }
                }
                else
                {
                    for (int i = 0; i < logCount; i++)
                    {
                        var log = cleanLogs.ElementAt(i);

                        if (log.Start == null && rawLogData.Item3)
                        {
                            log.Start = rawLogData.Item2;
                            log.IsComplete = true;
                            break;
                        }
                        else if (log.End == null && !rawLogData.Item3)
                        {
                            log.End = rawLogData.Item2;
                            log.IsComplete = true;
                            break;
                        }

                        if (rawLogData.Item3)
                        {
                            cleanLogData.Add(new LogData { Name = rawLogData.Item1, Start = rawLogData.Item2, End = null, IsComplete = false });
                            break;
                        }
                        else
                        {
                            cleanLogData.Add(new LogData { Name = rawLogData.Item1, Start = null, End = rawLogData.Item2, IsComplete = false });
                            break;
                        }
                    }
                }
            }

            var inCompleteSessions = cleanLogData.Where(x => !x.IsComplete);

            foreach (var session in inCompleteSessions)
            {
                if (session.Start == null)
                {
                    session.Start = session.End;
                }
                else if (session.End == null)
                {
                    session.End = session.Start;
                }
            }
        }

        // Returns the difference in TimeSpan between two nullable DateTime values.
        // If either date is null, returns TimeSpan.Zero.
        private static TimeSpan GetDateDifference(DateTime? date1, DateTime? date2)
        {
            if (date1 == null || date2 == null)
                return TimeSpan.Zero;

            return date1.Value - date2.Value;
        }
    }

    public class LogData
    {
        public string Name { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public bool IsComplete { get; set; }
    }
}
