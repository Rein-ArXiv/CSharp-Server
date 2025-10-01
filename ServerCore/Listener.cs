using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore
{
    /// <summary>
    /// TCP 리스너 - 클라이언트 연결 수락
    ///
    /// [CS 이론 - TCP 3-way Handshake]
    /// Client -> Server: SYN (연결 요청)
    /// Server -> Client: SYN+ACK (수락 + 연결 요청)
    /// Client -> Server: ACK (수락)
    /// → ESTABLISHED 상태
    ///
    /// [Backlog Queue]
    /// - SYN Queue: SYN만 받은 상태 (Half-open)
    /// - Accept Queue: 3-way 완료, Accept() 대기
    /// - Backlog 초과 시: 새 연결 거부 (SYN Cookie로 방어 가능)
    /// </summary>
    public class Listener
    {
        Socket _listenSocket;

        // Factory Pattern: 세션 생성을 외부에 위임
        Func<Session> _sessionFactory;

        /// <summary>
        /// 리스너 초기화 및 Accept 등록
        /// </summary>
        /// <param name="endPoint">바인딩할 IP:Port</param>
        /// <param name="sessionFactory">세션 생성 팩토리</param>
        /// <param name="register">동시 Accept 개수 (기본 10)</param>
        /// <param name="backlog">대기 큐 크기 (기본 100)</param>
        public void Init(IPEndPoint endPoint, Func<Session> sessionFactory, int register = 10, int backlog = 100)
        {
            // [1. 소켓 생성]
            // SocketType.Stream = TCP (연결 지향, 신뢰성)
            // ProtocolType.Tcp = TCP 프로토콜 명시
            _listenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _sessionFactory += sessionFactory;

            // [2. 바인딩]
            // IP:Port를 소켓에 할당
            // SO_REUSEADDR 고려 (재시작 시 TIME_WAIT 문제)
            _listenSocket.Bind(endPoint);

            // [3. 리스닝 시작]
            // backlog: Accept Queue 크기
            // SYN Flood 공격 대비: backlog 크게 + SYN Cookie
            _listenSocket.Listen(backlog);

            // [4. 다중 Accept 등록]
            // register개만큼 동시 Accept 대기
            // C10K 문제 해결: 비동기 I/O로 스레드 재사용
            for (int i = 0; i < register; i++)
            {
                SocketAsyncEventArgs args = new();
                args.Completed += new EventHandler<SocketAsyncEventArgs>(OnAcceptCompleted);
                RegisterAccept(args);
            }
        }

        /// <summary>
        /// Accept 등록 - 새 연결 대기
        /// </summary>
        void RegisterAccept(SocketAsyncEventArgs args)
        {
            // 이전 소켓 정리 (재사용 시 필수)
            args.AcceptSocket = null;

            // 비동기 Accept 시작
            bool pending = _listenSocket.AcceptAsync(args);

            // 동기 완료 시 직접 콜백 호출
            if (pending == false)
            {
                OnAcceptCompleted(null, args);
            }
        }

        /// <summary>
        /// Accept 완료 콜백 - IOCP 스레드풀에서 호출
        /// </summary>
        void OnAcceptCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError == SocketError.Success)
            {
                // [세션 생성 및 시작]
                // Factory Pattern으로 세션 타입 유연하게 결정
                Session session = _sessionFactory.Invoke();
                session.Start(args.AcceptSocket);
                session.OnConnected(args.AcceptSocket.RemoteEndPoint);

                // [최적화 포인트]
                // 여기서 Nagle 알고리즘 비활성화 가능:
                // args.AcceptSocket.NoDelay = true;
            }
            else
            {
                Console.WriteLine($"Accept failed: {args.SocketError.ToString()}");
            }

            // [다음 Accept 등록]
            // 한 연결 처리 후 즉시 다음 연결 대기 (연속성)
            RegisterAccept(args);
        }
    }
}
