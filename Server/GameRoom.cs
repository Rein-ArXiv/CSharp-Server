using Server;
using ServerCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server
{
    /// <summary>
    /// 게임 룸 - 세션 관리 및 브로드캐스팅
    ///
    /// [CS 이론 - 아키텍처 패턴]
    /// - Actor 모델: JobQueue로 동시성 문제 해결
    /// - Pub-Sub 패턴: BroadCast로 N:N 통신
    /// - Session Manager: 연결된 클라이언트 관리
    ///
    /// [스레드 모델]
    /// - I/O 스레드 (IOCP): OnRecvCompleted → Push
    /// - GameRoom 스레드 (논리적): JobQueue.Flush → BroadCast
    /// - 싱글 스레드 보장: _sessions에 lock 불필요
    ///
    /// [성능 고려사항]
    /// - O(N²) 브로드캐스팅: N세션 * M패킷
    /// - 개선: Interest Management (AOI), Spatial Hashing
    /// </summary>
    internal class GameRoom : IJobQueue
    {
        // 접속한 세션 목록 (JobQueue 내부에서만 접근 → Thread-safe)
        List<ClientSession> _sessions = new List<ClientSession>();

        // JobQueue 위임 (Actor 패턴)
        JobQueue _jobQueue = new JobQueue();

        // 브로드캐스트 대기 패킷 (Flush 시 일괄 전송)
        List<ArraySegment<byte>> _pendingList = new List<ArraySegment<byte>>();

        /// <summary>
        /// JobQueue에 작업 추가 (IJobQueue 구현)
        /// </summary>
        public void Push(Action job)
        {
            _jobQueue.Push(job);
        }

        /// <summary>
        /// 패킷 일괄 전송 - 모든 세션에게 브로드캐스트
        ///
        /// [시간 복잡도]
        /// - O(N * M): N = 세션 수, M = 패킷 수
        /// - 최악: 100 세션 * 100 패킷 = 10,000 Send 호출
        ///
        /// [최적화 방안]
        /// 1. Interest Management: 시야 범위 내만 전송
        /// 2. Spatial Hashing: 그리드 기반 필터링
        /// 3. 병렬 전송: Parallel.ForEach
        /// </summary>
        public void Flush()
        {
            // [브로드캐스트]
            // 모든 세션에게 동일한 패킷 리스트 전송
            foreach (ClientSession session in _sessions)
            {
                session.Send(_pendingList);
            }

            Console.WriteLine($"Flushed {_pendingList.Count} packets to {_sessions.Count} sessions.");

            // [패킷 리스트 초기화]
            // 다음 Flush를 위해 비움
            _pendingList.Clear();
        }

        /// <summary>
        /// 채팅 브로드캐스트 - 패킷 생성 및 대기열 추가
        /// </summary>
        /// <param name="sender">발신자 세션</param>
        /// <param name="chat">채팅 내용</param>
        public void BroadCast(ClientSession sender, string chat)
        {
            // [패킷 생성]
            S_Chat packet = new S_Chat();
            packet.playerId = sender.SessionId;
            packet.chat = $"{chat} I am {packet.playerId}";

            // [직렬화]
            // Packet → byte[] (네트워크 전송 형식)
            ArraySegment<byte> segment = packet.Write();

            // [대기열 추가]
            // Flush 호출 시 일괄 전송 (배칭)
            _pendingList.Add(segment);

            // [설계 고려]
            // 즉시 Send 안하는 이유:
            // 1. 패킷 배칭으로 시스템 콜 횟수 감소
            // 2. Flush 시점 제어 (틱 기반 전송)
        }

        /// <summary>
        /// 세션 입장 - GameRoom에 추가
        /// </summary>
        public void Enter(ClientSession session)
        {
            // [세션 등록]
            _sessions.Add(session);
            session.Room = this;

            // [입장 알림 브로드캐스트 추가 가능]
            // BroadCast(session, "님이 입장하셨습니다");
        }

        /// <summary>
        /// 세션 퇴장 - GameRoom에서 제거
        /// </summary>
        public void Leave(ClientSession session)
        {
            // [세션 제거]
            _sessions.Remove(session);

            // [퇴장 알림 브로드캐스트 추가 가능]
            // BroadCast(session, "님이 퇴장하셨습니다");
        }
    }
}
