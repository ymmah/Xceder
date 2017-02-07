using Com.Xceder.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Xceder
{
    [TestClass]
    public class TestClient
    {
        [TestMethod]
        public void TestLogin()
        {
            Client client = new Client();

            XcederStream stream = new Xceder.XcederStream(client);

            client.start("192.168.1.104", 8082, false);

            IConnectableObservable<Tuple<Request, Response>> loginResultStream = stream.RcvMsgStream.Where(ev => { return ev.Item1 != null && ev.Item1.RequestCase == Request.RequestOneofCase.Logon; }).Select(ev => { return ev.Item2.Result; }).Publish();
        }
    }
}
