using ServerCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Server
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
            ReadOnlySpan<byte> s = new ReadOnlySpan<byte>(segment.Array, segment.Offset, segment.Count);

            ushort count = 0;
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
            ushort nameLen = (ushort)Encoding.Unicode.GetByteCount(this.name);
            success &= BitConverter.TryWriteBytes(s.Slice(count, s.Length - count), nameLen);
            count += sizeof(ushort); // nameLen(2)
            Array.Copy(Encoding.Unicode.GetBytes(name), 0, segment.Array, count, nameLen);
            count += nameLen; // name(n)

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

    class ClientSession : PacketSession
    {
        public override void OnConnected(EndPoint endPoint)
        {
            Console.WriteLine($"Client connected: {endPoint}");

            try
            {
                //Packet packet = new Packet() { size = 100, packetId = 10 };

                //ArraySegment<byte> openSegment = SendBufferHelper.Open(4096);
                //byte[] packetSizeBuffer = BitConverter.GetBytes(packet.size);
                //byte[] packetIdBuffer = BitConverter.GetBytes(packet.packetId);

                //Array.Copy(packetSizeBuffer, 0, openSegment.Array, openSegment.Offset, packetSizeBuffer.Length);
                //Array.Copy(packetIdBuffer, 0, openSegment.Array, openSegment.Offset + packetSizeBuffer.Length, packetIdBuffer.Length);

                //ArraySegment<byte> sendBuff = SendBufferHelper.Close(packetIdBuffer.Length + packetSizeBuffer.Length);

                //Encoding.UTF8.GetBytes("Welcome to MMORPG Server!");
                //Send(sendBuff);

                Thread.Sleep(1000);
                Disconnect();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in OnConnected: {e.Message}");
            }
        }
        public override void OnRecvPacket(ArraySegment<byte> buffer)
        {
            ushort count = 0;
            ushort size = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
            count += 2; // size(2)

            ushort packetId = BitConverter.ToUInt16(buffer.Array, buffer.Offset + count);
            count += 2; // packetId(2)

            switch ((PacketID) packetId)
            {
                case PacketID.PlayerInfoReq:
                    {
                        PlayerInfoReq p = new PlayerInfoReq();
                        p.Read(buffer);
                        Console.WriteLine($"PlayerInfoReq: playerId={p.playerId}, Name={p.name}");
                    }
                    break;
            }
            Console.WriteLine($"Received packet: size={size}, packetId={packetId}");
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
