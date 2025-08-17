using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore
{
    public class RecvBuffer
    {
        ArraySegment<byte> _buffer;
        int _readPos;
        int _writePos;

        public RecvBuffer(int bufferSize)
        {
            _buffer = new ArraySegment<byte>(new byte[bufferSize], 0, bufferSize);
        }

        public int DataSize
        {
            get { return _writePos - _readPos;}
        }

        public int FreeSize
        {
            get { return _buffer.Count - _writePos; }
        }

        public ArraySegment<byte> ReadSegment()
        {
            return new ArraySegment<byte>(_buffer.Array, _buffer.Offset + _readPos, DataSize);
        }

        public ArraySegment<byte> WriteSegment()
        {
            return new ArraySegment<byte>(_buffer.Array, _buffer.Offset + _writePos, FreeSize);
        }

        public void Clean()
        {
            int dataSize = DataSize;
            if (dataSize == 0)
            {
                _readPos = 0;
                _writePos = 0;
            }
            else
            {
                Array.Copy(_buffer.Array, _buffer.Offset + _readPos, _buffer.Array, _buffer.Offset, dataSize);
                _readPos = 0;
                _writePos = dataSize;


            }
        }

        public bool OnRead(int numOfBytes)
        {
            if (numOfBytes < 0 || numOfBytes > DataSize)
            {
                //throw new ArgumentOutOfRangeException(nameof(numOfBytes), "Invalid number of bytes to read.");
                return false;
            }
            _readPos += numOfBytes;
            return true;
        }

        public bool OnWrite(int numOfBytes)
        {
            if (numOfBytes < 0 || numOfBytes > FreeSize)
            {
                //throw new ArgumentOutOfRangeException(nameof(numOfBytes), "Invalid number of bytes to write.");
                return false;
            }
            _writePos += numOfBytes;
            return true;
        }
    }
}
