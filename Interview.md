# 게임 서버 기술 면접 가이드

## 목차
1. [네트워크 기초](#1-네트워크-기초)
2. [동시성 제어](#2-동시성-제어)
3. [비동기 I/O](#3-비동기-io)
4. [메모리 관리](#4-메모리-관리)
5. [프로토콜 설계](#5-프로토콜-설계)
6. [아키텍처 설계](#6-아키텍처-설계)

---

## 1. 네트워크 기초

### Q1-1: TCP vs UDP를 비교하고, 왜 이 프로젝트에서 TCP를 선택했나요?

**기본 답변:**
- **TCP**: Connection-oriented, 신뢰성 보장(재전송, 순서 보장), 흐름 제어, 혼잡 제어
- **UDP**: Connectionless, 빠르지만 신뢰성 없음, 패킷 손실/순서 보장 안함
- **선택 이유**: 채팅 게임은 메시지 누락이 치명적이므로 TCP 선택

**꼬리 질문 1-1-1: TCP의 신뢰성은 어떻게 보장되나요?**

**답변:**
1. **Sequence Number**: 각 바이트에 번호를 매겨 순서 보장
2. **ACK (Acknowledgment)**: 수신 확인
3. **재전송 타이머**: ACK 안오면 재전송 (RTO - Retransmission Timeout)
4. **체크섬**: 데이터 무결성 검증

**코드 연결:** `Session.cs`의 `OnRecvCompleted`가 성공적으로 받으면 자동으로 다음 Recv 등록 → TCP 스택이 내부적으로 ACK 전송

**꼬리 질문 1-1-2: RTO는 어떻게 계산되나요? (Karn's Algorithm, Jacobson's Algorithm)**

**깊은 답변:**
```
SRTT (Smoothed RTT) = (1 - α) * SRTT + α * RTT_sample  // α = 1/8
RTTVAR = (1 - β) * RTTVAR + β * |SRTT - RTT_sample|   // β = 1/4
RTO = SRTT + 4 * RTTVAR
```
- **Exponential Backoff**: 재전송 실패 시 RTO를 2배씩 증가 (네트워크 혼잡 대응)
- **Karn's Algorithm**: 재전송된 패킷의 RTT는 측정에서 제외 (모호성 제거)

**면접 팁:** "TCP/IP Illustrated Vol.1의 21장을 공부했고, Wireshark로 실제 RTO 변화를 관찰해봤습니다" 같은 추가 멘트 효과적

---

### Q1-2: TCP의 Head-of-Line Blocking 문제를 설명하세요.

**기본 답변:**
- 패킷 #100이 손실되면, #101, #102가 먼저 도착해도 애플리케이션에 전달 안됨
- TCP는 순서를 보장하므로 #100 재전송 완료까지 블로킹
- **게임 영향**: 실시간성 저하 (FPS 게임에서는 치명적)

**코드 연결:** `PacketSession.cs:15-35`에서 헤더 크기만큼 안오면 `break` → TCP 버퍼에 데이터 쌓임

**꼬리 질문 1-2-1: 이 문제를 해결하려면 어떻게 해야 하나요?**

**답변:**
1. **UDP + 커스텀 신뢰성**: 중요 패킷만 ACK (Reliable UDP)
2. **HTTP/3 (QUIC)**: 스트림 단위 독립성 (Stream-level reliability)
3. **FEC (Forward Error Correction)**: 오류 정정 코드 추가
4. **패킷 우선순위**: 최신 데이터만 전송 (delta compression)

**꼬리 질문 1-2-2: QUIC은 어떻게 HOL Blocking을 해결하나요?**

**깊은 답변:**
- **멀티플렉싱**: 여러 스트림을 하나의 연결로 전송
- **스트림 독립성**: 스트림 A의 손실이 스트림 B에 영향 없음
- **0-RTT 연결**: 연결 재개 시 핸드셰이크 생략
- **UDP 기반**: 커널 업데이트 없이 프로토콜 개선 가능

**실무 예시:** Google의 GQUIC → IETF QUIC (RFC 9000), Cloudflare 사용 중

---

### Q1-3: Nagle 알고리즘이 무엇이고, 게임 서버에서 비활성화해야 하는 이유는?

**기본 답변:**
- **Nagle 알고리즘**: 작은 패킷을 모아서 전송 (네트워크 효율 향상)
- **조건**: ACK를 받거나 MSS(Maximum Segment Size)만큼 모일 때까지 대기
- **문제점**: 지연 시간 증가 (최대 200ms)

**코드 추가 필요:**
```csharp
// Listener.cs의 OnAcceptCompleted 또는 Connector.cs에 추가
socket.NoDelay = true;  // Nagle 알고리즘 비활성화
```

**꼬리 질문 1-3-1: NoDelay를 켜면 네트워크 오버헤드가 커지지 않나요?**

**답변:**
- **오버헤드**: TCP 헤더 20B + IP 헤더 20B = 40B (작은 페이로드면 비효율적)
- **트레이드오프**: 게임은 지연시간 < 대역폭 (10ms vs 1KB는 사용자 체감 가능)
- **해결책**:
  1. **Packet Batching**: 애플리케이션 레벨에서 모아서 전송 (`GameRoom.Flush`)
  2. **Corking**: `TCP_CORK` (Linux) - 명시적으로 모았다가 한번에 전송

**꼬리 질문 1-3-2: Delayed ACK와의 상호작용은?**

**깊은 답변:**
- **Delayed ACK**: 수신자가 ACK를 최대 200ms 지연 (패킷 절약)
- **Nagle + Delayed ACK**: 최악의 조합 (송신자는 ACK 대기, 수신자는 ACK 지연)
- **결과**: 40ms + 200ms = 240ms 지연 발생 가능
- **해결**: `TCP_NODELAY` + `TCP_QUICKACK` (Linux) 조합

**실무 경험 어필**: "테스트 환경에서 Nagle 켜고/끄고 핑 측정해봤더니 평균 15ms 차이 났습니다"

---

### Q1-4: TCP 3-way Handshake와 4-way Handshake를 설명하세요.

**기본 답변:**

**3-way Handshake (연결 수립):**
```
Client          Server
  |----SYN---->|   (SEQ=x)
  |<--SYN/ACK--|   (SEQ=y, ACK=x+1)
  |----ACK---->|   (ACK=y+1)
```

**4-way Handshake (연결 종료):**
```
Client          Server
  |----FIN---->|
  |<---ACK-----|
  |<---FIN-----|
  |----ACK---->|
```

**코드 연결:**
- `Listener.cs:22` `Listen(backlog)` → SYN queue 크기 설정
- `Session.cs:106` `Shutdown(Both)` → FIN 전송

**꼬리 질문 1-4-1: SYN Flood 공격은 무엇이고 어떻게 방어하나요?**

**답변:**
1. **공격 원리**: SYN만 보내고 ACK 안보냄 → Half-open 연결로 backlog queue 포화
2. **방어 기법**:
   - **SYN Cookie**: backlog 없이 상태 저장 (SEQ 번호에 정보 인코딩)
   - **타임아웃 단축**: SYN_RCVD 상태 타임아웃 감소
   - **방화벽**: Rate limiting, IP 블랙리스트

**꼬리 질문 1-4-2: SYN Cookie의 동작 원리는?**

**깊은 답변:**
```
SYN Cookie = hash(src_ip, src_port, dst_ip, dst_port, secret_key, timestamp)
```
1. SYN 받으면 backlog에 저장 안하고 쿠키 계산 → SYN/ACK 전송
2. ACK 받으면 쿠키 검증 → 유효하면 연결 수립
3. **장점**: 메모리 소비 없음
4. **단점**: TCP 옵션 일부 손실 (Window Scale, SACK)

**실무 연결**: Linux `net.ipv4.tcp_syncookies = 1`

---

### Q1-5: TIME_WAIT 상태가 무엇이고, 왜 필요한가요?

**기본 답변:**
- **TIME_WAIT**: 연결 종료 후 2*MSL(Maximum Segment Lifetime) 동안 대기 (보통 60초)
- **이유**:
  1. 마지막 ACK 손실 대비 (상대방의 FIN 재전송 처리)
  2. 이전 연결의 지연된 패킷이 새 연결과 섞이지 않도록

**코드 영향:**
```csharp
// Session.cs:106
_socket.Shutdown(SocketShutdown.Both);
_socket.Close();  // TIME_WAIT 상태 진입
```

**꼬리 질문 1-5-1: 서버 재시작 시 "Address already in use" 에러가 나는 이유는?**

**답변:**
- TIME_WAIT 상태의 소켓이 포트를 점유 중
- **해결책**:
```csharp
_listenSocket.SetSocketOption(SocketOptionLevel.Socket,
                               SocketOptionName.ReuseAddress, true);
```

**꼬리 질문 1-5-2: SO_REUSEADDR vs SO_REUSEPORT의 차이는?**

**깊은 답변:**
- **SO_REUSEADDR**: TIME_WAIT 소켓 재사용 허용 (서버용)
- **SO_REUSEPORT**: 여러 소켓이 같은 포트 바인딩 (로드밸런싱)
  - Linux 3.9+에서 지원
  - 커널이 연결을 여러 프로세스에 분산
  - **예시**: Nginx worker 프로세스들이 80 포트 공유

**실무 활용**: "멀티프로세스 서버에서 SO_REUSEPORT로 accept() 분산 처리 경험 있습니다"

---

## 2. 동시성 제어

### Q2-1: SpinLock과 Mutex의 차이점은? 언제 SpinLock을 사용해야 하나요?

**기본 답변:**

| 특성 | SpinLock | Mutex |
|------|----------|-------|
| 대기 방식 | Busy-waiting (CPU 점유) | Sleep (컨텍스트 스위칭) |
| 락 시간 | 짧을 때 유리 (< 수십 μs) | 길 때 유리 |
| 오버헤드 | 컨텍스트 스위칭 없음 | 커널 모드 전환 비용 |
| CPU 사용 | 높음 | 낮음 |

**코드 연결:** `Lock.cs:36-43` - 5000번 spin 후 `Thread.Yield()`

**꼬리 질문 2-1-1: Thread.Yield()와 Thread.Sleep(0)의 차이는?**

**답변:**
- **Thread.Yield()**:
  - 같은 우선순위의 다른 스레드에게만 양보
  - 실행 가능한 스레드 없으면 즉시 복귀
- **Thread.Sleep(0)**:
  - 더 낮은 우선순위 스레드에게도 양보
  - 스케줄러 재평가
- **Thread.Sleep(1)**:
  - 최소 1ms 대기 (Windows는 보통 15.6ms quantum)

**코드 개선 제안:**
```csharp
// Lock.cs 개선안
for (int i = 0; i < MAX_SPIN_COUNT; i++)
{
    if (Interlocked.CompareExchange(ref _flag, desired, EMPTY_FLAG) == EMPTY_FLAG)
    {
        _writeCount = 1;
        return;
    }
    // SpinWait 사용 (하드웨어 최적화)
    if (i < MAX_SPIN_COUNT / 2)
        Thread.SpinWait(1);  // PAUSE 명령어 (하이퍼스레딩 효율)
    else
        Thread.Sleep(0);     // 후반부는 양보
}
```

**꼬리 질문 2-1-2: PAUSE 명령어가 왜 필요한가요? (x86)**

**깊은 답변:**
- **문제**: Busy-waiting 시 파이프라인이 메모리 읽기로 포화
- **PAUSE 효과**:
  1. 파이프라인 플러시 방지 → 전력 절약
  2. 하이퍼스레딩 환경에서 물리 코어를 논리 코어에 양보
  3. Memory order violation 감소
- **성능**: 10-40 사이클 대기 (CPU마다 다름)

**ARM의 경우**: `YIELD` 명령어 (ARMv8)

---

### Q2-2: Reader-Writer Lock의 장점과 구현 원리는?

**기본 답변:**
- **목적**: 읽기는 동시 허용, 쓰기는 배타적
- **장점**: 읽기 위주 작업에서 성능 향상
- **이 프로젝트**: `Lock.cs:18` - 비트마스킹으로 구현

```
[Unused(1)] [WriteThreadId(15)] [ReadCount(16)]
```

**코드 연결:**
- `WRITE_MASK = 0x7FFF0000` - 상위 15비트
- `READ_MASK = 0x0000FFFF` - 하위 16비트

**꼬리 질문 2-2-1: Writer Starvation 문제는 무엇인가요?**

**답변:**
- **문제**: Reader가 계속 들어오면 Writer가 영원히 대기
- **이 코드의 상황**: `Lock.cs:69-73` - Writer 체크 없이 ReadCount 증가
- **해결 방법**:
  1. **Writer 우선순위**: Writer 대기 중이면 새 Reader 차단
  2. **공정성 보장**: FIFO 큐 사용
  3. **타임아웃**: 일정 시간 후 강제 진입

**코드 개선안:**
```csharp
// Writer 대기 플래그 추가
const int WRITER_WAITING_MASK = 0x80000000;

public void ReadLock()
{
    while (true)
    {
        int expected = _flag & READ_MASK;
        // Writer 대기 중이면 차단
        if ((_flag & (WRITE_MASK | WRITER_WAITING_MASK)) != 0)
        {
            Thread.Yield();
            continue;
        }
        if (Interlocked.CompareExchange(ref _flag, expected + 1, expected) == expected)
            return;
    }
}
```

**꼬리 질문 2-2-2: ReaderWriterLockSlim과의 차이는?**

**깊은 답변:**

| 특성 | 커스텀 Lock | ReaderWriterLockSlim |
|------|-------------|---------------------|
| 재귀 락 | 지원 (`_writeCount`) | UpgradeableRead로 지원 |
| Spin | 하드코딩 (5000) | 자동 조정 |
| 공정성 | 없음 | 없음 (Slim은 성능 우선) |
| 오버헤드 | 낮음 | 중간 |

**실무 비교**: "벤치마크 결과 커스텀 구현이 20% 빨랐지만, 유지보수성 고려해 ReaderWriterLockSlim 사용 권장"

---

### Q2-3: Interlocked.CompareExchange의 동작 원리는? (CAS)

**기본 답변:**
```csharp
// Atomic 연산 (CPU 레벨에서 보장)
int original = Interlocked.CompareExchange(ref location, value, comparand);

// 의사 코드:
original = location;
if (location == comparand)
    location = value;
return original;
```

**코드 연결:** `Lock.cs:38`, `Session.cs:101` - Disconnect 중복 방지

**꼬리 질문 2-3-1: CPU는 어떻게 원자성을 보장하나요?**

**답변:**
- **x86**: `LOCK CMPXCHG` 명령어
  1. **Bus Lock**: 메모리 버스 잠금 (멀티 코어 대기)
  2. **Cache Lock**: 캐시 라인 잠금 (최신 CPU, 더 빠름)
- **ARM**: `LDREX/STREX` (Load/Store Exclusive)
  ```
  retry:
    LDREX r1, [r0]      // Exclusive load
    CMP r1, r2          // Compare
    BNE fail
    STREX r3, r4, [r0]  // Exclusive store
    CMP r3, #0          // 성공 여부
    BNE retry           // 실패 시 재시도
  fail:
  ```

**꼬리 질문 2-3-2: ABA 문제는 무엇이고 어떻게 해결하나요?**

**깊은 답변:**

**문제 시나리오:**
```
시간    Thread 1        Thread 2
t1      Read A
t2                      A → B
t3                      B → A (재사용)
t4      CAS 성공! (하지만 다른 A)
```

**해결 방법:**
1. **버전 카운터**: 64비트로 확장 (포인터 + 카운터)
```csharp
struct Versioned<T> {
    T value;
    int version;
}
// 128비트 CAS 사용 (일부 플랫폼 지원)
```

2. **Hazard Pointer**: 사용 중인 포인터 보호
3. **Epoch-based reclamation**: 세대 기반 메모리 회수

**실무 예시**: Lock-free queue 구현 시 빈번히 발생

---

### Q2-4: JobQueue를 사용하는 이유는? Actor 모델과의 관계는?

**기본 답변:**
- **목적**: 멀티스레드 환경에서 싱글스레드처럼 동작 → 동시성 문제 제거
- **장점**: 락 없이 안전, 로직 단순화
- **단점**: 순차 처리로 병렬성 손실

**코드 연결:** `JobQueue.cs:20-36` - Push 시 첫 진입자만 Flush

**꼬리 질문 2-4-1: Actor 모델이 무엇인가요?**

**답변:**
- **개념**: "공유 메모리 없음, 메시지로만 통신"
- **구성 요소**:
  1. **Actor**: 상태 + 행동 캡슐화
  2. **Mailbox**: 메시지 큐 (이 프로젝트의 `_jobs`)
  3. **메시지**: 불변 객체
- **특징**:
  - 한 번에 하나의 메시지만 처리
  - 순서 보장
  - Location transparency (분산 시스템 확장 가능)

**유명 구현체**: Erlang, Akka(JVM), Orleans(.NET)

**꼬리 질문 2-4-2: 이 구현의 문제점은?**

**답변:**
1. **Head-of-line Blocking**: 긴 작업이 큐 전체를 막음
   ```csharp
   // GameRoom.cs의 BroadCast가 오래 걸리면?
   Push(() => BroadCast(session, longText));  // 뒤 작업 대기
   ```

2. **싱글 스레드 병목**: CPU 코어 하나만 사용

**개선안:**
```csharp
// 우선순위 큐 사용
PriorityQueue<Action> _jobs;

// 또는 워커 풀
class JobQueue {
    ThreadPool _workers = new ThreadPool(4);

    public void Push(Action job) {
        _workers.QueueWork(job);  // 병렬 처리
    }
}
```

**꼬리 질문 2-4-3: Flush의 재진입 방지는 어떻게 동작하나요?**

**깊은 답변:**
```csharp
// JobQueue.cs:20-30
public void Push(Action job)
{
    bool flush = false;
    lock (_lock)
    {
        _jobs.Enqueue(job);
        if (_flush == false)  // 첫 진입자만 true
        {
            flush = _flush = true;
        }
    }
    if (flush)  // 락 밖에서 Flush (데드락 방지)
    {
        Flush();
    }
}
```

**동작 흐름:**
```
Thread A: Push(job1) → flush=true → Flush 진입
Thread B: Push(job2) → flush=false (A가 이미 처리 중)
Thread A: Flush 내부에서 job1, job2 모두 처리
```

**설계 의도**: 컨텍스트 스위칭 최소화

---

## 3. 비동기 I/O

### Q3-1: SocketAsyncEventArgs를 사용하는 이유는? async/await과의 차이점은?

**기본 답변:**

| 특성 | SocketAsyncEventArgs | async/await |
|------|---------------------|-------------|
| 할당 | 재사용 (풀링) | 매번 Task 할당 |
| 성능 | 최고 | 중간 |
| 코드 | 복잡 | 간결 |
| GC 압박 | 없음 | 있음 |

**코드 연결:**
```csharp
// Session.cs:55-56
SocketAsyncEventArgs _sendArgs = new SocketAsyncEventArgs();
SocketAsyncEventArgs _recvArgs = new SocketAsyncEventArgs();
```

**꼬리 질문 3-1-1: IOCP(I/O Completion Port)란 무엇인가요?**

**답변:**
- **Windows 비동기 I/O 모델**:
  1. 애플리케이션: `ReceiveAsync()` 호출
  2. 커널: 데이터 도착 시 IOCP 큐에 완료 통지
  3. 스레드풀: `GetQueuedCompletionStatus()` 대기 중인 스레드가 처리
  4. 콜백: `OnRecvCompleted` 실행

**장점:**
- 스레드 수 < 연결 수 (C10K 해결)
- 커널이 로드밸런싱 (스레드 자동 할당)

**코드 흐름:**
```
Session.Start()
  → RegisterRecv()
    → ReceiveAsync()  ──┐
                        │ (비동기)
  ┌─────────────────────┘
  │ 데이터 도착
  └→ OnRecvCompleted()
       → RegisterRecv() (다시 등록)
```

**꼬리 질문 3-1-2: Linux의 epoll과 비교하면?**

**깊은 답변:**

| 특성 | IOCP (Windows) | epoll (Linux) |
|------|----------------|---------------|
| 모델 | Completion-based | Readiness-based |
| 통지 | 완료 후 | 준비되면 |
| 버퍼 | 커널이 관리 | 애플리케이션 관리 |
| 스레드 | 자동 할당 | 수동 할당 |

**epoll 사용 예:**
```csharp
// Linux에서의 구현 (의사 코드)
int epfd = epoll_create1(0);
epoll_ctl(epfd, EPOLL_CTL_ADD, socket_fd, &event);

while (true) {
    int n = epoll_wait(epfd, events, MAX_EVENTS, -1);
    for (int i = 0; i < n; i++) {
        if (events[i].events & EPOLLIN) {
            // 읽기 가능 → recv() 호출
            recv(events[i].data.fd, buffer, size, 0);
        }
    }
}
```

**IOCP는 recv()가 완료된 후 통지**, epoll은 읽기 가능하면 통지

---

### Q3-2: RegisterRecv가 재귀적으로 호출되지 않는 이유는?

**기본 답변:**
```csharp
// Session.cs:181
bool pending = _socket.ReceiveAsync(args);
if (pending == false)
{
    OnRecvCompleted(null, args);  // 동기 완료
}
```

- **pending = false**: 이미 데이터 도착 (동기 완료)
- **pending = true**: 비동기 대기

**문제점:**
```
RegisterRecv()
  → ReceiveAsync() → 동기 완료
    → OnRecvCompleted()
      → RegisterRecv()
        → ReceiveAsync() → 동기 완료
          → ... (무한 재귀)
```

**해결책:** 콜백에서 직접 호출 (재귀 아님)

**꼬리 질문 3-2-1: 왜 동기 완료가 발생하나요?**

**답변:**
- TCP 버퍼에 이미 데이터 존재
- 커널이 즉시 처리 가능
- **성능 최적화**: 컨텍스트 스위칭 비용 절약

**꼬리 질문 3-2-2: Stack Overflow를 방어하는 다른 방법은?**

**깊은 답변:**

**1. Trampoline 패턴:**
```csharp
void RegisterRecv(SocketAsyncEventArgs args)
{
    while (true)
    {
        bool pending = _socket.ReceiveAsync(args);
        if (pending) return;

        // 동기 완료 시 직접 처리
        OnRecvCompleted(null, args);
        // 루프로 다시 등록 (재귀 없음)
    }
}
```

**2. 깊이 제한:**
```csharp
int _recursionDepth = 0;
const int MAX_DEPTH = 10;

void OnRecvCompleted(object sender, SocketAsyncEventArgs args)
{
    if (++_recursionDepth > MAX_DEPTH)
    {
        ThreadPool.QueueUserWorkItem(_ => {
            _recursionDepth = 0;
            RegisterRecv(args);
        });
        return;
    }
    // 정상 처리
}
```

**실무 예시**: ASP.NET Core의 Kestrel도 유사한 기법 사용

---

### Q3-3: SendAsync의 BufferList vs Buffer의 차이는?

**기본 답변:**
```csharp
// Session.cs:122
_sendArgs.BufferList = _pendingList;  // Scatter-Gather I/O
```

- **Buffer**: 단일 연속 메모리
- **BufferList**: 여러 조각 (Vectored I/O)

**장점:**
- 메모리 복사 없이 여러 패킷 전송
- **Writev (Unix)**, **WSASend (Windows)** 내부 활용

**꼬리 질문 3-3-1: Scatter-Gather I/O란?**

**답변:**
- **Scatter (읽기)**: 커널이 여러 버퍼로 분산 저장
- **Gather (쓰기)**: 여러 버퍼를 모아서 한 번에 전송

**시스템 콜 비교:**
```c
// 기존 방식 (N번 호출)
for (int i = 0; i < N; i++)
    send(fd, buffers[i], sizes[i], 0);

// Gather 방식 (1번 호출)
struct iovec iov[N];
writev(fd, iov, N);
```

**성능 차이:** 시스템 콜 오버헤드 감소 (유저↔커널 모드 전환 비용)

**꼬리 질문 3-3-2: 왜 BufferList를 null로 초기화하나요?**

**깊은 답변:**
```csharp
// Session.cs:147
_sendArgs.BufferList = null;
_pendingList.Clear();
```

**이유:**
1. **메모리 누수 방지**: BufferList가 참조 유지 시 GC 불가
2. **재사용**: 다음 Send에서 새 BufferList 설정
3. **SocketAsyncEventArgs 내부 규칙**: Buffer와 BufferList 동시 사용 불가

**내부 동작:**
```csharp
// SocketAsyncEventArgs 내부 (추측)
if (BufferList != null)
    UsePinnedBufferList();
else if (Buffer != null)
    UseSingleBuffer();
```

---

## 4. 메모리 관리

### Q4-1: ArraySegment를 사용하는 이유는?

**기본 답변:**
- **목적**: 배열의 일부를 가리키는 구조체 (복사 없이)
- **구성**: `byte[] Array`, `int Offset`, `int Count`

**코드 연결:**
```csharp
// RecvBuffer.cs:32
public ArraySegment<byte> ReadSegment()
{
    return new ArraySegment<byte>(_buffer.Array, _buffer.Offset + _readPos, DataSize);
}
```

**꼬리 질문 4-1-1: Span<T>와의 차이는?**

**답변:**

| 특성 | ArraySegment<T> | Span<T> |
|------|----------------|---------|
| 타입 | class/struct | ref struct |
| 힙 할당 | 가능 | 불가능 (스택만) |
| 비동기 | 가능 | 불가능 |
| 성능 | 중간 | 최고 |
| 사용 | 필드, Task | 지역 변수 |

**코드 개선 (C# 7.2+):**
```csharp
// Span 사용 시 (동기 메서드만)
public Span<byte> ReadSegment()
{
    return new Span<byte>(_buffer.Array, _readPos, DataSize);
}

// Memory<T> 사용 시 (비동기 가능)
Memory<byte> _buffer;
public Memory<byte> ReadSegment()
{
    return _buffer.Slice(_readPos, DataSize);
}
```

**꼬리 질문 4-1-2: ref struct의 제약 사항은?**

**깊은 답변:**

**불가능한 것들:**
```csharp
class Container {
    Span<byte> field;  // ❌ 필드 불가
}

async Task Method() {
    Span<byte> span = ...;
    await Task.Delay(1);  // ❌ await 경계 넘기 불가
}

IEnumerable<int> Iterator() {
    Span<int> span = ...;
    yield return 1;  // ❌ yield 불가
}
```

**이유:** 스택에만 존재 → 힙/비동기 스택 프레임에 저장 불가

**실무 활용**: "Span으로 바꾸고 싶지만 SocketAsyncEventArgs가 ArraySegment 요구해서 유지"

---

### Q4-2: RecvBuffer의 Clean 함수가 Array.Copy를 사용하는 이유는?

**기본 답변:**
```csharp
// RecvBuffer.cs:50
Array.Copy(_buffer.Array, _buffer.Offset + _readPos,
           _buffer.Array, _buffer.Offset, dataSize);
_readPos = 0;
_writePos = dataSize;
```

**목적:** 버퍼 압축 (Compaction) - 읽은 공간 재사용

**시각적 설명:**
```
Before:
[----Read----|-----Data-----|-------Free-------]
             ↑_readPos      ↑_writePos

After Clean:
[-----Data-----|------------------Free---------]
↑_readPos=0    ↑_writePos
```

**꼬리 질문 4-2-1: 왜 매번 Clean하지 않고 조건부로 하나요?**

**답변:**
```csharp
// RecvBuffer.cs:43-46
if (dataSize == 0)
{
    _readPos = 0;
    _writePos = 0;  // 복사 없이 리셋
}
```

- **성능**: Array.Copy는 비용 높음 (최악 64KB 복사)
- **최적화**: 데이터 없으면 포인터만 리셋

**꼬리 질문 4-2-2: Ring Buffer로 개선할 수 있나요?**

**깊은 답변:**

**Ring Buffer 구현:**
```csharp
public class RingBuffer
{
    byte[] _buffer;
    int _readPos;
    int _writePos;
    int _capacity;

    public ArraySegment<byte> WriteSegment()
    {
        if (_writePos < _readPos)  // Wrap around
            return new ArraySegment<byte>(_buffer, _writePos, _readPos - _writePos);
        else
            return new ArraySegment<byte>(_buffer, _writePos, _capacity - _writePos);
    }

    public void OnWrite(int bytes)
    {
        _writePos = (_writePos + bytes) % _capacity;  // Circular
    }
}
```

**장점:** Array.Copy 불필요
**단점:**
1. 읽기가 2개 세그먼트로 나뉠 수 있음 (복잡도 증가)
2. SocketAsyncEventArgs는 연속 메모리 선호

**실무 선택**: "버퍼가 64KB로 작고, Clean 빈도가 낮아서 현재 구조 유지"

---

### Q4-3: GC 압박을 줄이기 위한 최적화 방법은?

**기본 답변:**

**1. Object Pooling:**
```csharp
// SendBuffer.cs (추정)
class SendBufferPool
{
    ConcurrentBag<byte[]> _pool = new();

    public byte[] Rent(int size)
    {
        if (_pool.TryTake(out byte[] buffer) && buffer.Length >= size)
            return buffer;
        return new byte[size];
    }

    public void Return(byte[] buffer)
    {
        _pool.Add(buffer);
    }
}
```

**2. ArrayPool<T> 사용 (.NET):**
```csharp
byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
try {
    // 사용
} finally {
    ArrayPool<byte>.Shared.Return(buffer);
}
```

**3. struct 활용:**
```csharp
// Packet을 struct로 변경 (힙 할당 제거)
public struct S_Chat  // class → struct
{
    public int playerId;
    public string chat;
}
```

**꼬리 질문 4-3-1: Gen0/1/2 GC의 차이는?**

**답변:**

| Generation | 수집 빈도 | 대상 | 비용 |
|-----------|---------|------|-----|
| Gen0 | 매우 높음 | 단명 객체 | 낮음 (1-2ms) |
| Gen1 | 중간 | Gen0 생존자 | 중간 |
| Gen2 | 낮음 | 장수 객체 | 높음 (100ms+) |

**최적화 전략:**
- Gen0 수집 늘리기 (단명 객체 빠른 해제)
- Gen2 수집 줄이기 (큰 객체 풀링)

**측정 도구:**
```csharp
GC.GetGeneration(obj);
GC.CollectionCount(0);  // Gen0 수집 횟수
```

**꼬리 질문 4-3-2: LOH(Large Object Heap)의 문제는?**

**깊은 답변:**

**특징:**
- 85,000 바이트 이상 객체
- Gen2에 직접 할당
- **압축 안됨** → 단편화 발생

**문제 시나리오:**
```
[----A(100KB)----|----B(100KB)----|----C(100KB)----]
Delete B
[----A(100KB)----|-----Hole-------|----C(100KB)----]
Allocate D(150KB)  // ❌ Hole에 안들어감 → 힙 확장
```

**해결책:**
```csharp
// .NET Core 2.1+
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect();

// 또는 애초에 큰 객체 피하기
byte[] _buffer = new byte[84000];  // LOH 회피
```

**실무 경험**: "모니터링 시 LOH 크기 증가 발견 → ArrayPool로 해결"

---

## 5. 프로토콜 설계

### Q5-1: TCP의 스트림 특성 때문에 발생하는 문제는?

**기본 답변:**

**문제 1: Packet Fragmentation (조각남)**
```
Send: [Packet A (100 bytes)]
Recv: [50 bytes] ... [50 bytes]  // 2번에 나눠 도착
```

**문제 2: Packet Coalescing (합쳐짐)**
```
Send: [Packet A (10 bytes)] [Packet B (10 bytes)]
Recv: [20 bytes]  // 한 번에 도착
```

**코드 연결:** `PacketSession.cs:15-35` - 반복문으로 패킷 단위 분리

**꼬리 질문 5-1-1: Framing을 어떻게 구현했나요?**

**답변:**
```csharp
// [size(2)] [packetId(2)] [data(n)]
while (true)
{
    if (buffer.Count < HeaderSize) break;  // 헤더 부족

    ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
    if (buffer.Count < dataSize) break;  // 전체 패킷 부족

    OnRecvPacket(...);  // 완전한 패킷 처리
    buffer = buffer.Slice(dataSize);  // 다음 패킷으로
}
```

**꼬리 질문 5-1-2: 다른 Framing 기법은?**

**깊은 답변:**

**1. Delimiter-based:**
```
"Hello\nWorld\n"  // \n으로 구분
```
- 장점: 간단
- 단점: Binary 데이터에 부적합, Escape 필요

**2. Fixed-length:**
```
[100 bytes][100 bytes][100 bytes]
```
- 장점: 파싱 빠름
- 단점: 공간 낭비

**3. Length-prefixed (현재 구조):**
```
[size][data][size][data]
```
- 장점: 효율적, Binary 안전
- 단점: 헤더 오버헤드

**4. Consistent Overhead Byte Stuffing (COBS):**
- 특정 바이트 (예: 0x00) 제거 후 인코딩
- 하드웨어 프로토콜에서 사용 (UART)

---

### Q5-2: Big Endian vs Little Endian - 고려했나요?

**기본 답변:**

```csharp
// Session.cs:23
ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
```

**문제점:**
- `BitConverter`는 **시스템 Endian** 사용
- Windows/Linux x86: Little Endian
- 네트워크 표준 (RFC): **Big Endian** (Network Byte Order)

**크로스 플랫폼 이슈:**
```
Client (x86, Little): [0x64, 0x00]  // 100
Server (ARM BE, Big): 0x0064 = 25600 ❌
```

**꼬리 질문 5-2-1: 어떻게 해결해야 하나요?**

**답변:**

**방법 1: 명시적 변환**
```csharp
ushort dataSize = IPAddress.NetworkToHostOrder(
    BitConverter.ToInt16(buffer.Array, buffer.Offset));
```

**방법 2: BinaryPrimitives (권장, .NET Core 2.1+)**
```csharp
using System.Buffers.Binary;

ushort dataSize = BinaryPrimitives.ReadUInt16BigEndian(
    buffer.AsSpan());

// 쓰기
BinaryPrimitives.WriteUInt16BigEndian(span, value);
```

**방법 3: 프로토콜 명세에 명시**
```
// 이 프로젝트: "모든 정수는 Little Endian"
// 클라이언트도 동일하게 구현 (일관성 중요)
```

**꼬리 질문 5-2-2: UTF-8 문자열은 Endian 영향 받나요?**

**깊은 답변:**

**영향 없음:**
- UTF-8은 바이트 단위 인코딩 (순서 고정)
```
"Hello" = [0x48, 0x65, 0x6C, 0x6C, 0x6F]  // 항상 동일
```

**영향 있음:**
- **UTF-16/UTF-32**: BOM (Byte Order Mark) 필요
```
UTF-16 LE: [0xFF, 0xFE] + data
UTF-16 BE: [0xFE, 0xFF] + data
```

**실무 권장:** "네트워크는 UTF-8 사용 (HTTP/2, gRPC 표준)"

---

### Q5-3: 최대 패킷 크기를 65535로 제한한 이유는?

**기본 답변:**
```csharp
// Session.cs:50
RecvBuffer _recvBuffer = new RecvBuffer(65535);

// PacketSession.cs:23
ushort dataSize = ...;  // 0 ~ 65535
```

**이유:**
1. **헤더 크기**: 2바이트 = 65536가지 값
2. **MTU 고려**: Ethernet MTU 1500 바이트보다 크게 여유
3. **메모리**: 세션당 64KB (1000 세션 = 64MB)

**꼬리 질문 5-3-1: MTU/MSS가 무엇인가요?**

**답변:**

**MTU (Maximum Transmission Unit):**
- 링크 계층에서 전송 가능한 최대 프레임 크기
- Ethernet: 1500 바이트
- PPPoE: 1492 바이트

**MSS (Maximum Segment Size):**
- TCP 페이로드 최대 크기
- MSS = MTU - IP헤더(20) - TCP헤더(20) = **1460 바이트**

**패킷 분할:**
```
애플리케이션: Send 10KB
TCP: 10KB / 1460 = 7개 세그먼트로 분할
IP: 각 세그먼트를 IP 패킷으로 캡슐화
Ethernet: 각 IP 패킷을 프레임으로 전송
```

**꼬리 질문 5-3-2: Path MTU Discovery는?**

**깊은 답변:**

**목적:** 경로상 최소 MTU 찾기 (단편화 방지)

**동작:**
1. DF (Don't Fragment) 플래그 설정하고 전송
2. 중간 라우터가 MTU 초과 시 ICMP "Fragmentation Needed" 전송
3. 송신자가 MTU 감소 후 재전송

**Windows 소켓 옵션:**
```csharp
socket.SetSocketOption(SocketOptionLevel.IP,
                       SocketOptionName.DontFragment, true);
```

**블랙홀 문제:**
- 방화벽이 ICMP 차단 → MTU 협상 실패
- **해결**: MSS Clamping (TCP 옵션으로 MSS 조정)

---

## 6. 아키텍처 설계

### Q6-1: 이 서버의 스레드 모델을 설명하세요.

**기본 답변:**

**구조:**
```
[IOCP 스레드풀]  ← 여러 스레드가 I/O 처리
      ↓
OnRecvCompleted / OnSendCompleted
      ↓
[JobQueue.Push]  ← 작업을 큐에 추가
      ↓
[GameRoom.Flush] ← 싱글 스레드로 순차 처리
```

**장점:**
- I/O는 병렬 (IOCP 효율)
- 게임 로직은 직렬 (동시성 문제 없음)

**꼬리 질문 6-1-1: IOCP 스레드풀 크기는 어떻게 결정하나요?**

**답변:**

**기본 공식:**
```
최적 스레드 수 = CPU 코어 수 * (1 + I/O 대기 시간 / CPU 시간)
```

**예시:**
- CPU: 8코어
- I/O 대기: 90%, CPU: 10%
- 최적: 8 * (1 + 90/10) = **80 스레드**

**Windows ThreadPool 자동 조정:**
```csharp
ThreadPool.GetMinThreads(out int workerMin, out int ioMin);
ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);

// 기본값: 최소 = 코어 수, 최대 = 32767
// 필요 시 수동 설정
ThreadPool.SetMinThreads(100, 100);
```

**꼬리 질문 6-1-2: ThreadPool Starvation은 무엇인가요?**

**깊은 답변:**

**문제 시나리오:**
```csharp
// OnRecvCompleted (IOCP 스레드)
Task.Run(() => {
    Thread.Sleep(1000);  // Blocking 작업
}).Wait();  // ❌ IOCP 스레드 점유
```

**결과:**
- IOCP 스레드가 모두 블로킹
- 새 I/O 완료 통지 처리 불가
- 서버 먹통

**해결:**
```csharp
// 블로킹 작업은 별도 스레드로
ThreadPool.QueueUserWorkItem(_ => {
    Thread.Sleep(1000);  // 워커 스레드 사용
});
```

**실무 모니터링:**
```csharp
ThreadPool.GetAvailableThreads(out int worker, out int io);
// io가 0에 가까우면 경고
```

---

### Q6-2: C10K 문제를 아는가? 몇 명까지 수용 가능한가?

**기본 답변:**

**C10K 문제:**
- 1999년 Dan Kegel 제안
- "하나의 서버로 동시 10,000 클라이언트 처리"
- 당시 문제: Thread-per-connection → 스레드 고갈

**이 서버의 해결책:**
- **IOCP**: 스레드 재사용
- **비동기 I/O**: 블로킹 없음
- **JobQueue**: 게임 로직 효율화

**수용 가능 수 계산:**
```
메모리: 16GB
세션당 메모리: 65KB (RecvBuffer) + 객체 오버헤드 ≈ 100KB
최대 세션: 16GB / 100KB = 160,000 세션 (이론상)
```

**실제 제약:**
1. **포트 고갈**: 클라이언트 포트 65535개 (NAT 고려)
2. **CPU**: GameRoom.Flush가 O(N²)
3. **대역폭**: 100Mbps → 10KB/s * 10000 = 1Gbps 필요

**꼬리 질문 6-2-1: C10M (천만) 달성 방법은?**

**깊은 답변:**

**1. 커널 바이패스:**
- **DPDK (Data Plane Development Kit)**: 유저 공간에서 직접 NIC 제어
- **io_uring (Linux 5.1+)**: 시스템 콜 없는 I/O

**2. Zero-copy:**
```csharp
// sendfile() 시스템 콜 (Linux)
sendfile(socket_fd, file_fd, offset, count);
// 유저 공간 복사 없이 전송
```

**3. CPU 친화성 (Affinity):**
```csharp
Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(0xFF);  // 8코어 모두 사용
Thread.BeginThreadAffinity();  // 스레드를 특정 코어에 고정
```

**4. NUMA 인식:**
- 메모리와 CPU를 같은 노드에 할당

**실무 사례:** "WhatsApp이 200만 동시 연결 달성 (Erlang + FreeBSD)"

---

### Q6-3: GameRoom의 Flush가 O(N²)인 이유는?

**기본 답변:**
```csharp
// GameRoom.cs:26-29
foreach (ClientSession session in _sessions)  // N번
{
    session.Send(_pendingList);  // M개 패킷
}
```

- **시간 복잡도**: O(N * M)
- N = 세션 수, M = 패킷 수
- **최악**: 100 세션 * 100 패킷 = 10,000 작업

**꼬리 질문 6-3-1: 어떻게 최적화할 수 있나요?**

**답변:**

**1. Interest Management (관심 영역):**
```csharp
class GameRoom
{
    Dictionary<Vector2, List<ClientSession>> _grid;  // 그리드 기반

    public void BroadCast(ClientSession sender, string chat)
    {
        Vector2 cell = GetCell(sender.Position);
        // 같은 셀 + 인접 셀에만 전송
        foreach (var nearbySession in GetNearby(cell))
        {
            nearbySession.Send(...);
        }
    }
}
```

**2. Spatial Hashing:**
```csharp
// 100m x 100m 맵 → 10x10 그리드
int cellX = (int)(player.X / 10);
int cellY = (int)(player.Y / 10);
int key = cellX * 1000 + cellY;
```

**3. 비동기 전송:**
```csharp
Parallel.ForEach(_sessions, session => {
    session.Send(_pendingList);
});
```

**꼬리 질문 6-3-2: MMO 게임의 AOI 알고리즘은?**

**깊은 답변:**

**1. Grid (현재 개선안):**
- 장점: 구현 간단
- 단점: 경계 처리 복잡

**2. Quad-Tree:**
```
+-------+-------+
|   1   |   2   |
+---+---+---+---+
| 3 | 4 | 5 | 6 |
+---+---+---+---+
```
- 재귀적 분할
- 밀집 지역 자동 세분화

**3. 9-Cell (십자가):**
```
[NW][N][NE]
[ W][C][ E]
[SW][S][SE]
```
- 자신 + 8방향 셀만 관심
- World of Warcraft 사용

**4. Distance-based:**
```csharp
if (Vector2.Distance(player1.Pos, player2.Pos) < ViewRange)
    NotifyVisible(player1, player2);
```
- 정확하지만 O(N²)

**최신 기법:** **Lightsout (Amazon Lumberyard)** - SIMD 활용한 병렬 거리 계산

---

## 7. 심화 시나리오 질문

### Q7-1: 서버가 갑자기 느려졌습니다. 어떻게 디버깅하나요?

**체계적 접근:**

**1. 메트릭 수집:**
```csharp
// 성능 카운터
var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
var memCounter = new PerformanceCounter("Memory", "Available MBytes");

// GC 정보
GC.CollectionCount(0);
GC.GetTotalMemory(false);

// ThreadPool
ThreadPool.GetAvailableThreads(out int worker, out int io);
```

**2. 프로파일링:**
- **PerfView**: CPU 샘플링, ETW 이벤트
- **dotMemory**: 메모리 누수 추적
- **Wireshark**: 네트워크 패킷 분석

**3. 가설 검증:**
```
가설 1: GC 과부하 → Gen2 수집 횟수 확인
가설 2: ThreadPool 고갈 → GetAvailableThreads() 확인
가설 3: Lock 경합 → PerfView로 스핀락 대기 시간 측정
```

**꼬리 질문 7-1-1: Deadlock을 어떻게 감지하나요?**

**답변:**

**증상:**
- ThreadPool 스레드 모두 대기
- CPU 사용률 0%

**감지 도구:**
```csharp
// Visual Studio Debugger: Debug → Windows → Threads
// WinDbg: !locks, !syncblk

// 프로그래밍 방식
var threads = Process.GetCurrentProcess().Threads;
foreach (ProcessThread thread in threads)
{
    if (thread.ThreadState == ThreadState.Wait)
        Console.WriteLine($"Thread {thread.Id} is waiting");
}
```

**예방:**
1. Lock 순서 고정 (Lock A → Lock B 항상 동일)
2. 타임아웃 사용
```csharp
if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(5)))
    throw new DeadlockException();
```

**꼬리 질문 7-1-2: Livelock은 무엇인가요?**

**깊은 답변:**

**정의:** 스레드가 계속 실행되지만 진전 없음

**예시:**
```csharp
// Thread 1
while (true)
{
    if (Interlocked.CompareExchange(ref _flag, 1, 0) == 0)
        break;
    Thread.Yield();  // 양보 후 재시도
}

// Thread 2 (동일)
```

→ 둘 다 계속 재시도하지만 동시에 실패 (확률적)

**해결:** 랜덤 백오프
```csharp
int delay = Random.Shared.Next(1, 10);
Thread.Sleep(delay);
```

---

### Q7-2: 패킷이 손상되어 들어왔습니다. 어떻게 처리하나요?

**현재 코드 분석:**
```csharp
// PacketSession.cs:23
ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
if (buffer.Count < dataSize) break;
```

**취약점:**
- `dataSize`가 65535라면? → 계속 대기 (DoS 가능)
- `dataSize`가 0이라면? → 무한 루프

**개선안:**
```csharp
const ushort MAX_PACKET_SIZE = 4096;  // 실제 최대 크기

ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);

// 검증
if (dataSize < HeaderSize || dataSize > MAX_PACKET_SIZE)
{
    Console.WriteLine($"Invalid packet size: {dataSize}");
    Disconnect();  // 연결 끊기
    return 0;
}

if (buffer.Count < dataSize) break;
```

**꼬리 질문 7-2-1: 체크섬은 어떻게 구현하나요?**

**답변:**

**1. CRC32 (간단):**
```csharp
using System.IO.Hashing;

byte[] data = ...;
var crc = new Crc32();
crc.Append(data);
uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(crc.GetHashAndReset());
```

**2. HMAC (보안):**
```csharp
using var hmac = new HMACSHA256(key);
byte[] hash = hmac.ComputeHash(data);
```

**프로토콜:**
```
[size(2)] [packetId(2)] [checksum(4)] [data(n)]
```

**꼬리 질문 7-2-2: 암호화는 어떻게 추가하나요?**

**깊은 답변:**

**1. TLS (권장):**
```csharp
var sslStream = new SslStream(new NetworkStream(socket));
await sslStream.AuthenticateAsServerAsync(certificate);
```

**2. 애플리케이션 레벨 암호화:**
```csharp
using var aes = Aes.Create();
aes.Key = key;
aes.IV = iv;

using var encryptor = aes.CreateEncryptor();
byte[] encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
```

**트레이드오프:**
- **TLS**: CPU 오버헤드 20-30%, 표준 준수
- **커스텀**: 최적화 가능, 구현 실수 위험

**실무 선택:** "게임은 TLS (로그인/결제) + 커스텀 (게임 패킷)"

---

## 8. 실무 경험 어필 팁

### 프로젝트 설명 시:

**Before:**
"C# 게임 서버를 만들었습니다. TCP를 사용하고, 비동기로 처리했습니다."

**After:**
"IOCP 기반 비동기 I/O로 C10K 문제를 해결한 게임 서버를 설계했습니다. SocketAsyncEventArgs를 재사용해 GC 압박을 줄였고, JobQueue 패턴으로 Actor 모델을 구현해 동시성 문제를 해결했습니다. 부하 테스트 결과 500 동시 연결에서 평균 응답 시간 15ms, 99 percentile 50ms를 달성했습니다."

### 측정 가능한 성과:
```csharp
// 벤치마킹 코드
var stopwatch = Stopwatch.StartNew();
for (int i = 0; i < 10000; i++)
{
    client.Send(packet);
    await client.ReceiveAsync();
}
stopwatch.Stop();
Console.WriteLine($"Average latency: {stopwatch.ElapsedMilliseconds / 10000.0}ms");
```

### 개선 사례:
"SpinLock의 MAX_SPIN_COUNT를 5000 → 2000으로 줄였더니 CPU 사용률이 30% 감소했지만 지연시간이 5ms 증가해서 원복했습니다."

---

## 9. 추천 학습 자료

### 네트워크:
- **"TCP/IP Illustrated Volume 1"** - Richard Stevens (바이블)
- **"Unix Network Programming"** - Stevens (소켓 API 깊이)

### 동시성:
- **"C# Concurrency Cookbook"** - Stephen Cleary
- **"The Art of Multiprocessor Programming"** - Herlihy

### 시스템:
- **"Windows via C/C++"** - Jeffrey Richter (IOCP 챕터)
- **"Systems Performance"** - Brendan Gregg (프로파일링)

### 게임 서버:
- **"Multiplayer Game Programming"** - Joshua Glazer
- **Glenn Fiedler 블로그**: https://gafferongames.com/

### 실습:
- Wireshark로 자신의 서버 패킷 캡처
- PerfView로 CPU 프로파일링
- BenchmarkDotNet으로 마이크로 벤치마킹

---

**면접 마지막 질문: "질문 있으신가요?"**

**좋은 역질문:**
- "현재 서버 아키텍처의 최대 병목은 무엇인가요?"
- "동시 접속자 수 목표와 현재 수치는 어떻게 되나요?"
- "서버 팀에서 가장 도전적이었던 기술 문제는 무엇이었나요?"
- "코드 리뷰 문화와 테스팅 전략은 어떻게 되나요?"

**피해야 할 질문:**
- "연봉/복지는?" (최종 단계에서)
- "야근 많나요?" (부정적 인상)
