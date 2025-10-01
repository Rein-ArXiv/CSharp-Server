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

    /// <summary>
    /// 작업 큐 - Actor 모델 기반 동시성 제어
    ///
    /// [CS 이론 - Actor Model]
    /// - Actor: 상태 + 메시지 큐 + 순차 처리
    /// - 원칙: 1) 공유 메모리 없음, 2) 메시지로만 통신, 3) 동시 실행 없음
    /// - 장점: 동시성 문제(Race condition, Deadlock) 원천 차단
    /// - 유명 구현: Erlang, Akka, Orleans
    ///
    /// [이 구현의 특징]
    /// - 싱글 스레드 실행: 한 번에 하나의 작업만 처리
    /// - 재진입 방지: _flush 플래그로 중복 실행 차단
    /// - Lock-free 아님: _lock 사용 (하지만 짧은 임계 영역)
    /// </summary>
    public class JobQueue : IJobQueue
    {
        Queue<Action> _jobs = new Queue<Action>();
        object _lock = new object();

        // Flush 실행 중 플래그 (재진입 방지)
        bool _flush = false;

        /// <summary>
        /// 작업 추가 - 메시지 전송
        /// </summary>
        /// <param name="job">실행할 작업 (람다/델리게이트)</param>
        public void Push(Action job)
        {
            bool flush = false;

            // [1. 큐에 작업 추가]
            lock (_lock)
            {
                _jobs.Enqueue(job);

                // [2. 첫 진입자 결정]
                // _flush == false → 아무도 Flush 실행 중 아님
                // 첫 진입자만 flush = true로 설정
                if (_flush == false)
                {
                    flush = _flush = true;  // 본인이 Flush 책임
                }
                // else: 다른 스레드가 Flush 중 → 큐에만 추가하고 리턴
            }

            // [3. 락 밖에서 Flush 실행]
            // 왜 락 밖? → Flush가 오래 걸리면 다른 Push 블로킹
            // 데드락 방지: Flush 내부에서 또 Push 호출 가능
            if (flush)
            {
                Flush();  // 큐가 빌 때까지 모든 작업 실행
            }
        }

        /// <summary>
        /// 작업 일괄 실행 - 큐가 빌 때까지 순차 처리
        /// </summary>
        void Flush()
        {
            while (true)
            {
                Action action = Pop();
                if (action == null)
                {
                    // [큐 비었음 → Flush 종료]
                    _flush = false;  // 다음 Push가 Flush 실행 가능
                    return;
                }

                // [작업 실행]
                // 싱글 스레드 보장: 여기서 실행되는 작업들은 순차적
                // 동시성 문제 없음: 같은 GameRoom의 상태를 안전하게 변경 가능
                action.Invoke();

                // [문제점]
                // - Head-of-line Blocking: 긴 작업이 큐 전체를 막음
                // - CPU 단일 코어만 사용: 병렬성 손실
                // 개선: 워커 스레드풀, 우선순위 큐 등
            }
        }

        /// <summary>
        /// 작업 꺼내기 - Thread-safe
        /// </summary>
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
