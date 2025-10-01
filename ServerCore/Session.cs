using System.Net;
using System.Net.Sockets;

namespace ServerCore
{
    /// <summary>
    /// 패킷 기반 세션 - TCP 스트림을 패킷 단위로 분리 (Framing)
    ///
    /// [CS 이론]
    /// - TCP는 스트림 프로토콜 → 메시지 경계 없음
    /// - Packet Fragmentation: 하나의 패킷이 여러 번에 나눠 도착
    /// - Packet Coalescing: 여러 패킷이 한 번에 도착
    /// - 해결: Length-prefixed framing [size(2)][packetId(2)][data(n)]
    /// </summary>
    public abstract class PacketSession : Session
    {
        /// <summary>
        /// 패킷 헤더 크기 (size 필드)
        /// - 2바이트 = ushort = 최대 65535 크기
        /// - 트레이드오프: 작으면 큰 패킷 불가, 크면 오버헤드
        /// </summary>
        public static readonly int HeaderSize = 2;

        /// <summary>
        /// TCP 스트림에서 완전한 패킷들을 추출
        /// </summary>
        /// <param name="buffer">수신 버퍼 (여러 패킷 포함 가능)</param>
        /// <returns>처리한 바이트 수</returns>
        public sealed override int OnRecv(ArraySegment<byte> buffer)
        {
            // [size(2)] [packetId(2)] [ data(n) ]
            int processLen = 0;
            int packetCount = 0;

            // [패킷 분리 루프]
            // TCP의 스트림 특성상 한 번에 여러 패킷이 올 수 있으므로 반복 처리
            while (true)
            {
                // [1단계: 헤더 검증]
                // 최소한 size 필드(2바이트)는 있어야 함
                if (buffer.Count < HeaderSize)
                {
                    // 헤더가 부족한 경우 → 다음 Recv 대기
                    break;
                }

                // [2단계: 패킷 크기 읽기]
                // BitConverter: 시스템 Endian 사용 (x86은 Little Endian)
                // 주의: 크로스 플랫폼 시 BinaryPrimitives.ReadUInt16LittleEndian 권장
                ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);

                // [3단계: 완전성 검증]
                // 전체 패킷이 도착했는지 확인 (Fragmentation 대응)
                if (buffer.Count < dataSize)
                {
                    // 패킷 일부만 도착 → 나머지 대기
                    break;
                }

                // [4단계: 완전한 패킷 처리]
                OnRecvPacket(new ArraySegment<byte>(buffer.Array, buffer.Offset, dataSize));
                packetCount++;

                // [5단계: 버퍼 포인터 이동]
                processLen += dataSize;
                buffer = new ArraySegment<byte>(buffer.Array, buffer.Offset + dataSize, buffer.Count - dataSize);
                // ArraySegment는 복사 없이 포인터만 이동 (Zero-copy)
            }

            if (packetCount > 1)
                Console.WriteLine($"[PacketSession] OnRecv: {packetCount} packets");

            return processLen;
        }

        /// <summary>
        /// 파생 클래스에서 구현: 완전한 패킷 처리
        /// </summary>
        public abstract void OnRecvPacket(ArraySegment<byte> buffer);
    }

    /// <summary>
    /// TCP 세션 기본 클래스 - 비동기 I/O 기반 네트워크 통신
    ///
    /// [CS 이론]
    /// - IOCP (I/O Completion Port): Windows 비동기 I/O 모델
    ///   1. 애플리케이션: ReceiveAsync() 호출
    ///   2. 커널: 데이터 도착 시 완료 통지
    ///   3. 스레드풀: 대기 중인 스레드가 콜백 실행
    /// - Proactor Pattern: 완료 기반 (vs Reactor는 준비 기반)
    /// - Zero-copy: ArraySegment로 버퍼 복사 최소화
    /// </summary>
    public abstract class Session
    {
        Socket _socket;

        // [동시성 제어 - Interlocked]
        // Disconnect 중복 호출 방지 (CAS: Compare-And-Swap)
        // CPU의 LOCK CMPXCHG 명령어 사용 (원자적 연산)
        int _disconnected = 0;

        // [메모리 관리]
        // 65535 = ushort 최대값 (패킷 size 필드 2바이트)
        // Circular Buffer 패턴으로 재사용
        RecvBuffer _recvBuffer = new RecvBuffer(65535);

        // [송신 큐 - Producer-Consumer Pattern]
        object _lock = new object();  // 경쟁 조건(Race Condition) 방지
        Queue<ArraySegment<byte>> _sendQueue = new Queue<ArraySegment<byte>>();  // 대기 큐
        List<ArraySegment<byte>> _pendingList = new List<ArraySegment<byte>>();  // 전송 중 리스트

        // [비동기 이벤트 재사용]
        // SocketAsyncEventArgs: GC 압박 감소 (매번 할당 안함)
        // async/await보다 성능 좋음 (Task 할당 없음)
        SocketAsyncEventArgs _sendArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs _recvArgs = new SocketAsyncEventArgs();

        public abstract void OnConnected(EndPoint endPoint);
        public abstract int OnRecv(ArraySegment<byte> buffer);
        public abstract void OnSend(int numOfBytes);
        public abstract void OnDisconnected(EndPoint endPoint);

        void Clear()
        {
            lock (_lock)
            {
                _sendQueue.Clear();
                _pendingList.Clear();
            }
        }

        public void Start(Socket socket)
        {
            _socket = socket;
            _recvArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnRecvCompleted);
            _sendArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSendCompleted);
            RegisterRecv(_recvArgs);
        }

        public void Send(List<ArraySegment<byte>> sendBuffList)
        {
            if (sendBuffList == null || sendBuffList.Count == 0)
                return;

            lock (_lock)
            {
                foreach (ArraySegment<byte> sendBuff in sendBuffList)
                {
                    _sendQueue.Enqueue(sendBuff);
                }
                
                if (_pendingList.Count == 0)
                {
                    RegisterSend();
                }
            }
        }

        /// <summary>
        /// 연결 종료 - 멀티스레드 환경에서 안전하게 한 번만 실행
        /// </summary>
        public void Disconnect()
        {
            // [CAS (Compare-And-Swap) 패턴]
            // _disconnected를 1로 바꾸고, 이전 값 반환
            // 이전 값이 1이면 → 이미 다른 스레드가 Disconnect 호출
            // 원자적(Atomic) 연산: CPU의 LOCK CMPXCHG 명령어
            if (Interlocked.Exchange(ref _disconnected, 1) == 1)
            {
                return; // 이미 Disconnect가 호출된 경우
            }

            OnDisconnected(_socket.RemoteEndPoint);

            // [TCP 4-way Handshake 시작]
            // Shutdown: FIN 패킷 전송 (우아한 종료)
            // Both = Send + Receive 모두 차단
            _socket.Shutdown(SocketShutdown.Both);

            // [소켓 리소스 해제]
            // TIME_WAIT 상태 진입 (2*MSL 동안 대기, 보통 60초)
            // 이유: 1) 마지막 ACK 손실 대비, 2) 지연 패킷 방지
            _socket.Close();

            Clear();
        }

        #region 네트워크 통신

        /// <summary>
        /// 송신 등록 - Scatter-Gather I/O 사용
        /// </summary>
        void RegisterSend()
        {
            if (_disconnected == 1) return;

            // [1. 큐에서 전송할 버퍼들 수집]
            while (_sendQueue.Count > 0)
            {
                ArraySegment<byte> buff = _sendQueue.Dequeue();
                _pendingList.Add(buff);
            }

            // [2. Scatter-Gather I/O 설정]
            // BufferList: 여러 버퍼를 한 번에 전송 (Vectored I/O)
            // 내부적으로 WSASend (Windows) / writev (Linux) 호출
            // 장점: 메모리 복사 없이 여러 패킷 전송 (시스템 콜 횟수 감소)
            _sendArgs.BufferList = _pendingList;

            try
            {
                // [3. 비동기 송신 시작]
                bool pending = _socket.SendAsync(_sendArgs);

                // pending == false: 동기 완료 (버퍼에 즉시 복사 완료)
                // pending == true: 비동기 대기 (나중에 OnSendCompleted 호출됨)
                if (pending == false)
                {
                    // 동기 완료 시 직접 콜백 호출 (재귀 방지)
                    OnSendCompleted(null, _sendArgs);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"RegisterSend Failed {e}");
                //Disconnect();
            }
        }

        /// <summary>
        /// 송신 완료 콜백 - IOCP 스레드풀에서 호출
        /// </summary>
        void OnSendCompleted(object sender, SocketAsyncEventArgs args)
        {
            // [임계 영역 보호]
            // 여러 IOCP 스레드가 동시에 접근 가능 → lock 필요
            lock (_lock)
            {
                if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
                {
                    try
                    {
                        // [버퍼 정리]
                        // BufferList를 null로 설정 안하면 메모리 누수 (GC 불가)
                        // SocketAsyncEventArgs 재사용 위해 필수
                        _sendArgs.BufferList = null;
                        _pendingList.Clear();

                        OnSend(_sendArgs.BytesTransferred);

                        // [연쇄 송신]
                        // 전송 중에 새 패킷이 큐에 추가되었으면 계속 전송
                        if (_sendQueue.Count > 0)
                        {
                            RegisterSend();
                        }

                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"OnSendCompleted Failed {e}");
                    }
                }
                else
                {
                    // [오류 처리]
                    // BytesTransferred == 0: 상대방이 연결 종료
                    // SocketError != Success: 네트워크 오류 (타임아웃, RST 등)
                    Console.WriteLine($"OnSendCompleted Failed {args.SocketError}");
                    Disconnect();
                }
            }
        }

        /// <summary>
        /// 수신 등록 - RecvBuffer에 쓰기 위치부터 수신
        /// </summary>
        void RegisterRecv(SocketAsyncEventArgs args)
        {
            if (_disconnected == 1) return;

            // [1. 버퍼 정리]
            // 읽은 데이터 제거, 남은 데이터 앞으로 이동 (Compaction)
            _recvBuffer.Clean();

            // [2. 쓰기 가능 영역 설정]
            ArraySegment<byte> segment = _recvBuffer.WriteSegment();
            _recvArgs.SetBuffer(segment.Array, segment.Offset, segment.Count);

            try
            {
                // [3. 비동기 수신 시작]
                bool pending = _socket.ReceiveAsync(args);

                // [재귀 방지 패턴]
                // pending == false: 이미 데이터 있음 (동기 완료)
                // 콜백에서 직접 호출하여 스택 오버플로우 방지
                if (pending == false)
                {
                    OnRecvCompleted(null, args);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"RegisterRecv Failed {e}");
                // Disconnect();
            }
        }

        /// <summary>
        /// 수신 완료 콜백 - IOCP 스레드풀에서 호출
        /// </summary>
        void OnRecvCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
            {
                try
                {
                    // [1. 수신 버퍼 업데이트]
                    // WritePos 이동 (수신한 만큼)
                    if (_recvBuffer.OnWrite(args.BytesTransferred) == false)
                    {
                        // 버퍼 오버플로우 (공격 가능성)
                        Console.WriteLine($"OnRecvCompleted Failed: Buffer overflow");
                        Disconnect();
                        return;
                    }

                    // [2. 패킷 파싱]
                    // OnRecv: 완전한 패킷들을 추출하여 처리
                    // 반환값: 처리한 바이트 수
                    int processLen = OnRecv(_recvBuffer.ReadSegment());

                    // [3. 검증]
                    if (processLen < 0 || processLen > _recvBuffer.DataSize)
                    {
                        // 잘못된 반환값 → 프로토콜 위반
                        Disconnect();
                        return;
                    }

                    // [4. 읽기 포인터 이동]
                    if (_recvBuffer.OnRead(processLen) == false)
                    {
                        Disconnect();
                        return;
                    }

                    // [5. 다음 수신 등록]
                    // 재귀가 아닌 새로운 비동기 작업 시작
                    RegisterRecv(args);
                }

                catch (Exception e)
                {
                    Console.WriteLine($"OnRecvCompleted Failed {e}");
                }
            }
            else
            {
                // [연결 종료 감지]
                // BytesTransferred == 0: FIN 받음 (정상 종료)
                // SocketError != Success: 네트워크 오류
                Disconnect();
            }
        }
        #endregion
    }
}
