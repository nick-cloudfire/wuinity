using System;
using System.Threading;

namespace PREACT
{
    public sealed class JobWorker
    {
        private readonly Thread _thread;
        private readonly JobQueue _queue;
        private readonly AutoResetEvent _wakeEvent = new AutoResetEvent(false);
        private readonly CancellationToken _token;
        private readonly int _index;

        public JobWorker(JobQueue queue, CancellationToken token, int index)
        {
            _queue = queue;
            _token = token;
            _index = index;

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "JobWorker-" + index
            };

            _thread.Start();
        }

        public void Wake()
        {
            _wakeEvent.Set();
        }

        /// <summary>
        /// Runs jobs until cancelled, surviving a job that throws.
        ///
        /// The catch is what keeps the worker alive, and that matters more than it looks. An exception used
        /// to leave this method entirely - the finally still released the job, so the step that scheduled it
        /// completed and the simulation carried on - and killed the thread on its way out. With one module
        /// throwing every step, the workers died one per step until none was left, and the next
        /// <see cref="JobSystem.ExecuteJobs"/> spun on "pending > 0" against nobody able to run anything.
        ///
        /// What that looks like from outside is a simulation that stops dead a second or two in, with no
        /// error: the exception was lost with the thread, and the hang happens later and somewhere else.
        /// Reported and survived instead, so a module that throws is a message naming it, every step, and
        /// the run continues far enough to say so.
        /// </summary>
        private void Run()
        {
            while (!_token.IsCancellationRequested)
            {
                _wakeEvent.WaitOne();

                while (!_token.IsCancellationRequested)
                {
                    if (_queue.TryDequeue(out IJob job))
                    {
                        try
                        {
                            job.Execute();
                        }
                        catch (Exception e)
                        {
                            //With the stack, because the useful part is which module threw and where - the
                            //message alone ("Object reference not set to an instance of an object") names
                            //nothing at all.
                            Engine.Message(null, Engine.LogType.Exception,
                                "A simulation module threw on worker thread " + _index + ": " + e.Message
                                + System.Environment.NewLine + e.StackTrace);
                        }
                        finally
                        {
                            _queue.JobCompleted();
                        }
                    }
                    else
                    {
                        break; // no more jobs
                    }
                }
            }
        }
    }
}
