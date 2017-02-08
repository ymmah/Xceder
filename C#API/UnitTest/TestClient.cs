using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Reactive.Linq;
using Com.Xceder.Messages;

namespace Xceder
{
    [TestClass]
    public class TestClient
    {
        private bool LastSentHasError;
        private Request.RequestOneofCase LastSentRequestType;

        [TestMethod]
        public void TestLogin()
        {
            var client = new Client();

            var stream = new XcederStream(client);

            stream.SendMsgStream.Subscribe(onSendMsg);
            stream.RcvMsgStream.Subscribe(onRcvMsg);
            stream.RcvErrorStream.Subscribe(onRcvError);

            var task = client.connect("192.168.1.106", 81);

            try
            {
                task.Wait();
            }
            catch(Exception ex)
            {

            }

            Assert.AreEqual(task.Status, TaskStatus.Faulted);

            task = client.connect("192.168.1.106", 8082);

            task.Wait();

            Assert.IsTrue(task.Result);

            LastSentHasError = false;
            LastSentRequestType = Request.RequestOneofCase.Logon;

            client.sendLogin("dreamer", "test");

            Thread.Sleep(100000);
        }

        private void onRcvError(Exception ex)
        {

        }

        private void onRcvMsg(Tuple<Request, Response> tuple)
        {

        }

        private void onSendMsg(Tuple<Request, Exception> tuple)
        {
            Assert.AreEqual(tuple.Item1.RequestCase, LastSentRequestType);

            if (LastSentHasError)
                Assert.IsNotNull(tuple.Item2);
            else
                Assert.IsNull(tuple.Item2);
        }
    }
}
