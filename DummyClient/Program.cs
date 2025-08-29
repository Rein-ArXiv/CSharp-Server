
using ServerCore;
using System.Net;
using System.Net.Sockets;
using System.Text;


namespace DummyClient
{
    class Program
    {
        static void Main(string[] args)
        {
            string host = Dns.GetHostName();
            IPHostEntry ipHost = Dns.GetHostEntry(host);
            //Console.WriteLine(ipHost.ToString());
            IPAddress ipAddr = ipHost.AddressList[0];
            //Console.WriteLine(ipAddr.ToString());

            IPEndPoint endPoint = new IPEndPoint(ipAddr, 8888);

            Connector connector = new Connector();

            try
            {
                connector.Connect(endPoint, () => { return SessionManager.Instance.Generate(); }, 500);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Connection failed: {e.Message}");
                return;
            }

            while (true)
            {   
                try
                {
                    SessionManager.Instance.SendForEach();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error during send: {e.Message}");
                }
                Thread.Sleep(250);
            }
        }
    }
}