using ServerCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DummyClient
{
    public abstract class Packet
    {
        public ushort size;
        public ushort packetId;

        public abstract ArraySegment<byte> Write();
        public abstract void Read(ArraySegment<byte> buffer);
    }

    class PlayerInfoReq : Packet
    {
        public long playerId;
        public string name;

        public PlayerInfoReq()
        {
            this.packetId = (ushort)PacketID.PlayerInfoReq;
        }
        public override void Read(ArraySegment<byte> segment)
        {
            ushort count = 0;
            
            ReadOnlySpan<byte> s = new ReadOnlySpan<byte>(segment.Array, segment.Offset, segment.Count);
            count += sizeof(ushort); // size(2)
            count += sizeof(ushort); // packetId(2)

            this.playerId = BitConverter.ToInt64(s.Slice(count, s.Length - count));

            count += sizeof(long);

            // string
            ushort nameLen = BitConverter.ToUInt16(s.Slice(count, s.Length - count));
            count += sizeof(ushort); // nameLen(2)

            this.name = Encoding.Unicode.GetString(s.Slice(count, nameLen));
        }

        public override ArraySegment<byte> Write()
        {
            ArraySegment<byte> segment = SendBufferHelper.Open(4096);
            bool success = true;
            ushort count = 0;

            Span<byte> s = new Span<byte>(segment.Array, segment.Offset, segment.Count);

            count += sizeof(ushort); // size(2)
            success &= BitConverter.TryWriteBytes(s.Slice(count, s.Length - count), this.packetId);
            count += sizeof(ushort); // packetId(2)
            success &= BitConverter.TryWriteBytes(s.Slice(count, s.Length - count), this.playerId);
            count += sizeof(long); // playerId(8)

            // string
            //ushort nameLen = (ushort)Encoding.Unicode.GetByteCount(name);
            //success &= BitConverter.TryWriteBytes(s.Slice(count, s.Length - count), nameLen);
            //count += sizeof(ushort); // nameLen(2)
            //Array.Copy(Encoding.Unicode.GetBytes(name), 0, segment.Array, count, nameLen);
            //count += nameLen; // name(n)

            ushort nameLen = (ushort) Encoding.Unicode.GetBytes(this.name, 0, this.name.Length, segment.Array, segment.Offset + count + sizeof(ushort));
            success &= BitConverter.TryWriteBytes(s.Slice(count, s.Length - count), nameLen);

            count += sizeof(ushort);    // nameLen(2)
            count += nameLen;           // name(n)


            success &= BitConverter.TryWriteBytes(s, count);

            if (success == false)
            {
                return null;
                //throw new InvalidOperationException("Failed to write packet data.");
            }

            return SendBufferHelper.Close(count);
        }
    }

    public enum PacketID
    {
        PlayerInfoReq = 1,
        PlayerInfoOk = 2,
    }

    class ServerSession : Session
    {
        public override void OnConnected(EndPoint endPoint)
        {
            Console.WriteLine($"Connected To {endPoint}");

            PlayerInfoReq packet = new PlayerInfoReq()
            {
                packetId = (ushort)PacketID.PlayerInfoReq,
                playerId = 1001,
                name = "NPC"
            };

            for (int i = 0; i < 5; i++)
            {
                ArraySegment<byte> s = packet.Write();
                if (s!= null)
                {
                    Send(s);
                }
            }
        }

        // 이동 패킷
        public override int OnRecv(ArraySegment<byte> buffer)
        {
            string recvData = Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count);
            Console.WriteLine($"[From Server] {recvData}");
            return buffer.Count;
        }
        public override void OnSend(int numOfBytes)
        {
            Console.WriteLine($"Transferred {numOfBytes} bytes to client.");
        }
        public override void OnDisconnected(EndPoint endPoint)
        {
            Console.WriteLine($"OnDisconnected: {endPoint}");
        }
    }


}
