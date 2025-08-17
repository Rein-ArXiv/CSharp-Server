using System.Net;
using System.Text;
using ServerCore;

namespace Server;
class GameSession : Session
{
    class Packet
    {
        public ushort size;
        public ushort packetId;
    }

    class LoginOkPacket : Packet
    {
        public int attack;
    }

    public override void OnConnected(EndPoint endPoint)
    {
        Console.WriteLine($"Client connected: {endPoint}");

        try
        {

            // 예시로 Knight 객체를 생성하고 직렬화
            Packet knight = new Packet { hp = 100, attack = 50 };

            ArraySegment<byte> openSegment = SendBufferHelper.Open(4096);
            byte[] buffer = BitConverter.GetBytes(knight.hp);
            byte[] buffer2 = BitConverter.GetBytes(knight.attack);

            Array.Copy(buffer, 0, openSegment.Array, openSegment.Offset, buffer.Length);
            Array.Copy(buffer2, 0, openSegment.Array, openSegment.Offset + buffer.Length, buffer2.Length);

            ArraySegment<byte> sendBuff = SendBufferHelper.Close(buffer.Length + buffer2.Length);

            //Encoding.UTF8.GetBytes("Welcome to MMORPG Server!");
            Send(sendBuff);
            Thread.Sleep(1000);
            Disconnect();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error in OnConnected: {e.Message}");
        }
    }
    public override int OnRecv(ArraySegment<byte> buffer)
    {
        string recvData = Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count);
        Console.WriteLine($"[From Client] {recvData}");
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
class Program
{
    static Listener _listener = new Listener();


    static void Main(string[] args)
    {
        string host = Dns.GetHostName();
        IPHostEntry ipHost = Dns.GetHostEntry(host);
        IPAddress ipAddr = ipHost.AddressList[0];
        IPEndPoint endPoint = new IPEndPoint(ipAddr, 7777);

        _listener.Init(endPoint, () => { return new GameSession(); });
        Console.WriteLine("Listening...");

        while (true)
        {
            ;
        }

    }
}
