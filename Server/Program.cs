using System.Net;
using System.Text;
using ServerCore;

namespace Server;

class Program
{
    static Listener _listener = new Listener();
    public static GameRoom Room = new GameRoom();

    static void FlushRoom()
    {
        Room.Push(() => Room.Flush());
        JobTimer.Instance.Push(FlushRoom, 250);
    }

    static void Main(string[] args)
    {
        string host = Dns.GetHostName();
        IPHostEntry ipHost = Dns.GetHostEntry(host);
        //Console.WriteLine(ipHost.ToString());

        IPAddress ipAddr = ipHost.AddressList[0];
        //Console.WriteLine(ipAddr.ToString());

        IPEndPoint endPoint = new IPEndPoint(ipAddr, 8888);

        _listener.Init(endPoint, () => { return SessionManager.Instance.Generate(); });
        Console.WriteLine("Listening...");


        JobTimer.Instance.Push(FlushRoom);
        //FlushRoom();
        while (true)
        {
            JobTimer.Instance.Flush();
        }

    }
}
