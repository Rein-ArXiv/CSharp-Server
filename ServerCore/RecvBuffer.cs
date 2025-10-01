using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore
{
    /// <summary>
    /// 수신 버퍼 - Circular Buffer의 선형 변형
    ///
    /// [CS 이론 - Buffer Management]
    /// - Circular Buffer: 읽기/쓰기 포인터가 순환 (Array.Copy 불필요)
    /// - Linear Buffer: 포인터가 끝에 도달하면 압축(Compaction) 필요
    /// - 이 구현: Linear (단순함) + Clean으로 압축
    ///
    /// [메모리 레이아웃]
    /// [----Read----|-----Data-----|-------Free-------]
    ///              ↑_readPos      ↑_writePos
    ///
    /// [GC 최적화]
    /// - ArraySegment: Zero-copy (포인터만 이동)
    /// - 재사용: 한 번 할당 후 계속 사용
    /// </summary>
    public class RecvBuffer
    {
        ArraySegment<byte> _buffer;
        int _readPos;   // 다음 읽을 위치
        int _writePos;  // 다음 쓸 위치

        public RecvBuffer(int bufferSize)
        {
            _buffer = new ArraySegment<byte>(new byte[bufferSize], 0, bufferSize);
        }

        /// <summary>
        /// 읽지 않은 데이터 크기
        /// </summary>
        public int DataSize
        {
            get { return _writePos - _readPos;}
        }

        /// <summary>
        /// 쓰기 가능한 여유 공간
        /// </summary>
        public int FreeSize
        {
            get { return _buffer.Count - _writePos; }
        }

        /// <summary>
        /// 읽기 영역 반환 (Zero-copy)
        /// </summary>
        public ArraySegment<byte> ReadSegment()
        {
            return new ArraySegment<byte>(_buffer.Array, _buffer.Offset + _readPos, DataSize);
        }

        /// <summary>
        /// 쓰기 영역 반환 (Zero-copy)
        /// </summary>
        public ArraySegment<byte> WriteSegment()
        {
            return new ArraySegment<byte>(_buffer.Array, _buffer.Offset + _writePos, FreeSize);
        }

        /// <summary>
        /// 버퍼 압축 (Compaction) - 읽은 영역 재사용
        ///
        /// [최적화]
        /// - 데이터 없으면 포인터만 리셋 (O(1))
        /// - 데이터 있으면 앞으로 이동 (O(N))
        ///
        /// [메모리 작업]
        /// Before: [----Read----|---Data---|---Free---]
        /// After:  [---Data---|-------------Free------]
        /// </summary>
        public void Clean()
        {
            int dataSize = DataSize;
            if (dataSize == 0)
            {
                // [최적화] 데이터 없으면 Array.Copy 생략
                _readPos = 0;
                _writePos = 0;
            }
            else
            {
                // [Compaction] 읽은 공간 재활용
                // memmove와 유사 (중복 영역 안전 복사)
                Array.Copy(_buffer.Array, _buffer.Offset + _readPos,
                          _buffer.Array, _buffer.Offset, dataSize);
                _readPos = 0;
                _writePos = dataSize;

                // [성능 고려]
                // Array.Copy는 최적화되어 있음 (블록 복사)
                // 하지만 64KB면 비용 발생 → 빈도 중요
            }
        }

        /// <summary>
        /// 읽기 완료 알림 - 읽기 포인터 이동
        /// </summary>
        /// <param name="numOfBytes">읽은 바이트 수</param>
        /// <returns>성공 여부</returns>
        public bool OnRead(int numOfBytes)
        {
            // [검증] 범위 체크 (버퍼 오버런 방지)
            if (numOfBytes < 0 || numOfBytes > DataSize)
            {
                // 잘못된 사용 → 프로토콜 오류 가능성
                return false;
            }

            // [포인터 이동]
            // DataSize = _writePos - _readPos 감소
            _readPos += numOfBytes;
            return true;
        }

        /// <summary>
        /// 쓰기 완료 알림 - 쓰기 포인터 이동
        /// </summary>
        /// <param name="numOfBytes">쓴 바이트 수 (ReceiveAsync 결과)</param>
        /// <returns>성공 여부</returns>
        public bool OnWrite(int numOfBytes)
        {
            // [검증] 오버플로우 체크 (공격 방어)
            if (numOfBytes < 0 || numOfBytes > FreeSize)
            {
                // 버퍼 경계 초과 → DoS 공격 가능성
                return false;
            }

            // [포인터 이동]
            // DataSize = _writePos - _readPos 증가
            _writePos += numOfBytes;
            return true;
        }
    }
}
