using System;
using System.Threading;

namespace PREACT
{
    public sealed class JobSystem : IDisposable
    {
        private readonly JobQueue _queue = new JobQueue();
        private readonly JobWorker[] _workers;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public JobSystem(int workerCount)
        {
            if (workerCount <= 0)
            {
                workerCount = Environment.ProcessorCount;
            }                

            _workers = new JobWorker[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                _workers[i] = new JobWorker(_queue, _cts.Token, i);
            }                
        }

        public void Schedule(IJob job)
        {
            _queue.Enqueue(job);
        }

        public void Schedule(Action action)
        {
            Schedule(new LambdaJob(action));
        }

        /// <summary>How long the wait for a step's jobs may go on before it is called a hang.</summary>
        private const int StuckAfterSeconds = 30;

        public void ExecuteJobs()
        {
            // Wake all workers
            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i].Wake();
            }

            // Spin until all jobs are done
            bool reported = false;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (_queue.Pending > 0)
            {
                //Said once, rather than spinning in silence for as long as the application is open. A step
                //of a wildfire or a traffic network can legitimately take seconds, so this is deliberately
                //far longer than a slow step - it is here for the case where the jobs are never going to
                //finish, which is otherwise indistinguishable from the simulation still working.
                if (!reported && waited.Elapsed.TotalSeconds > StuckAfterSeconds)
                {
                    reported = true;
                    Engine.Message(null, Engine.LogType.Warning,
                        $"A simulation step has been waiting {StuckAfterSeconds} s for {_queue.Pending} module job(s) "
                        + "to finish. If the time never advances again, a module is stuck rather than slow; the "
                        + "console above will name it if it threw.");
                }

                Thread.Yield();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();

            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i].Wake();
            }                

            _cts.Dispose();
        }
    }
}
