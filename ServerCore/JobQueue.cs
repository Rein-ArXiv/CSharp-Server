using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore
{
    public interface IJobQueue
    {
        void Push(Action job);
    }

    public class JobQueue : IJobQueue
    {
        Queue<Action> _jobs = new Queue<Action>();
        object _lock = new object();
        bool _flush = false;

        public void Push(Action job)
        {
            bool flush = false;
            lock (_lock)
            {
                _jobs.Enqueue(job);
                if (_flush == false)
                {
                    flush = _flush = true;
                }
            }

            if (flush)
            {
                Flush();
            }
        }

        void Flush()
        {
            while (true)
            {
                Action action = Pop();
                if (action == null)
                {
                    _flush = false;
                    return;
                }
                action.Invoke();
            }
        }

        public Action Pop()
        {
            lock (_lock)
            {
                if (_jobs.Count == 0)
                    return null;
                return _jobs.Dequeue();
            }
        }
    }
}
