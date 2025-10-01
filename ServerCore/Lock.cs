using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore
{
    /// <summary>
    /// 재귀적 Reader-Writer SpinLock
    ///
    /// [CS 이론 - Spinlock vs Mutex]
    /// - SpinLock: Busy-waiting (CPU 점유) → 짧은 락에 유리
    /// - Mutex: Sleep (컨텍스트 스위칭) → 긴 락에 유리
    ///
    /// [Reader-Writer Lock]
    /// - 읽기: 동시 접근 허용 (공유 락)
    /// - 쓰기: 배타적 접근 (독점 락)
    /// - 목적: 읽기 위주 작업 성능 향상
    ///
    /// [Spin 정책]
    /// - 5000번 시도 → Thread.Yield() (양보)
    /// - 트레이드오프: Spin 많이 = CPU 낭비 / 적게 = 컨텍스트 스위칭
    /// </summary>
    internal class Lock
    {
        const int EMPTY_FLAG = 0x00000000;
        const int WRITE_MASK = 0x7FFF0000;  // 상위 15비트
        const int READ_MASK = 0x0000FFFF;   // 하위 16비트
        const int MAX_SPIN_COUNT = 5000;

        /// <summary>
        /// 비트 필드 구조
        /// [Unused(1)] [WriteThreadId(15)] [ReadCount(16)]
        ///
        /// 예시:
        /// - 쓰기 락 (Thread 123): 0x007B0000
        /// - 읽기 5개: 0x00000005
        /// - Thread 123의 쓰기 중 읽기 2개: 0x007B0002
        /// </summary>
        int _flag = EMPTY_FLAG;

        // 재귀적 쓰기 락 깊이 (같은 스레드의 중첩 락 허용)
        int _writeCount = 0;

        /// <summary>
        /// 쓰기 락 획득 - 배타적 접근
        /// </summary>
        public void WriteLock()
        {
            // [1. 재귀 락 체크]
            // 같은 스레드가 이미 쓰기 락 보유 시 카운트만 증가
            int lockThreadId = (_flag & WRITE_MASK) >> 16;
            if (Thread.CurrentThread.ManagedThreadId == lockThreadId)
            {
                _writeCount++;  // 재진입
                return;
            }

            // [2. 락 경합]
            // 아무도 없을 때만 획득 (읽기/쓰기 모두 없어야 함)
            int desired = (Thread.CurrentThread.ManagedThreadId << 16) & WRITE_MASK;

            while (true)
            {
                // [Spinlock] 5000번 시도
                for (int i = 0; i < MAX_SPIN_COUNT; i++)
                {
                    // [CAS (Compare-And-Swap)]
                    // _flag가 EMPTY면 desired로 변경 (원자적)
                    // CPU의 LOCK CMPXCHG 명령어 사용
                    if (Interlocked.CompareExchange(ref _flag, desired, EMPTY_FLAG) == EMPTY_FLAG)
                    {
                        _writeCount = 1;
                        return;  // 락 획득 성공
                    }
                    // Thread.SpinWait(1); // PAUSE 명령어 (CPU 최적화)
                }

                // [양보] 5000번 실패 시 다른 스레드에게 양보
                // 같은 우선순위 스레드에게만 양보
                Thread.Yield();
            }
        }

        /// <summary>
        /// 쓰기 락 해제
        /// </summary>
        public void WriteUnlock()
        {
            // [재귀 카운트 감소]
            int lockCount = --_writeCount;
            if (lockCount == 0)
            {
                // [락 완전 해제]
                // Interlocked 사용 (메모리 배리어 보장)
                Interlocked.Exchange(ref _flag, EMPTY_FLAG);
            }
        }

        /// <summary>
        /// 읽기 락 획득 - 공유 접근
        /// </summary>
        public void ReadLock()
        {
            // [1. 쓰기 락 보유자 체크]
            // 자기 자신이 쓰기 락 보유 중이면 읽기도 허용 (재진입)
            int lockThreadId = (_flag & WRITE_MASK) >> 16;
            if (Thread.CurrentThread.ManagedThreadId == lockThreadId)
            {
                Interlocked.Increment(ref _flag);  // ReadCount 증가
                return;
            }

            // [2. 읽기 락 획득]
            // 쓰기 락 없을 때 ReadCount 증가 (여러 스레드 동시 가능)
            while (true)
            {
                for (int i = 0; i < MAX_SPIN_COUNT; i++)
                {
                    // [CAS로 ReadCount 증가]
                    // expected: 현재 ReadCount (WRITE_MASK 부분은 0이어야 함)
                    int expected = (_flag & READ_MASK);

                    // 쓰기 락 없으면 (WRITE_MASK == 0) ReadCount 증가
                    if (Interlocked.CompareExchange(ref _flag, expected + 1, expected) == expected)
                        return;

                    // [Writer Starvation 문제]
                    // 읽기가 계속 들어오면 쓰기가 영원히 대기 가능
                    // 개선: Writer 대기 플래그 추가하여 새 Reader 차단
                }

                // [무한 Spin 방지]
                // Yield 누락 → CPU 100% 점유 (배터리/열 문제)
                // Thread.Yield();  // ← 원본 코드 버그! 추가 필요
            }
        }

        /// <summary>
        /// 읽기 락 해제
        /// </summary>
        public void ReadUnlock()
        {
            // [ReadCount 감소]
            // Decrement는 원자적 (메모리 배리어 포함)
            Interlocked.Decrement(ref _flag);
        }
    }
}
