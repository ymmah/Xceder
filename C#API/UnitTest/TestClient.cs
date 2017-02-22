using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Reactive.Linq;
using Com.Xceder.Messages;
using System.Collections.Generic;
using System.Diagnostics;

namespace Xceder
{
    [TestClass]
    public class TestClient
    {
        private bool lastSentHasError;
        private Request.RequestOneofCase lastSentRequestType;
        private ERRORCODE lastSentRequestResultCode;

        private bool isResponded = false;

        [TestMethod]
        public void TestLogin()
        {
            login();
        }

        [TestMethod]
        public void TestSubscribePrice()
        {
            var stream = login();
            var client = stream.XcederClient;
            var lastMonth = DateTime.Now.AddMonths(-1).ToString("yyyyMM");

            var expiryInLastMonthInstrumentList = queryInstrument(client, "ES", Instrument.Types.PRODUCT.Fut, Exchange.Types.EXCHANGE.Cme, lastMonth);

            Debug.WriteLine("maturity/expiry date is in last month ({0}) counters are {1}", lastMonth, expiryInLastMonthInstrumentList.Count);

            var instrumentList = queryInstrument(client, "ES", Instrument.Types.PRODUCT.Fut, Exchange.Types.EXCHANGE.Cme);

            var instrument = instrumentList[0];

            Debug.WriteLine("returned total counters are {0}, fist instrument (maturityDate:{1}):{2}", instrumentList.Count, Client.convertEpochDays(instrument.MaturityEpochDays), instrument);

            var requestTask = client.subscribePrice(instrument.Identity.Id);

            setLastSentRequestExpectation(false, Request.RequestOneofCase.MarketData);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            Debug.WriteLine("returned market data for subscription: {0}", requestTask.Result.Item2.MarketDatas);

            waitResponse(10);
        }

        [TestMethod]
        public void TestOrder()
        {
            var stream = login();

            var client = stream.XcederClient;

            var instruments = queryInstrument(client, "ES", Instrument.Types.PRODUCT.Fut, Exchange.Types.EXCHANGE.Cme);

            OrderParams orderParams = new OrderParams();

            orderParams.Instrument = instruments[0].Identity.Id;
            orderParams.LimitPrice = 2;
            orderParams.Side = SIDE.Buy;
            orderParams.OrderQty = 10;
            orderParams.OrderType = Order.Types.TYPE.Lmt;
            orderParams.TimeInForce = Order.Types.TIMEINFORCE.Day;

            var requestTask = client.submitOrder(orderParams);

            setLastSentRequestExpectation(false, Request.RequestOneofCase.Order, ERRORCODE.NoTradingaccount);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            Debug.WriteLine("returned order status report for submission: {0}", requestTask.Result.Item2.OrderReports);

            var orderReportAry = requestTask.Result.Item2.OrderReports.OrderReport;

            var executionReportAry = orderReportAry[orderReportAry.Count - 1].Report;

            var lastestExecReport = executionReportAry[executionReportAry.Count - 1];

            //withdraw the order need use the clOrdID, which is returned by the server when submit the order
            requestTask = client.replaceOrWithdrawOrder(lastestExecReport.OrderID.ClOrdID, 0, 0, 0);

            requestTask.Wait();

            Debug.WriteLine("returned order status report for withdraw request: {0}", requestTask.Result.Item2.OrderReports);

            waitResponse(10);
        }


        private IList<Instrument> queryInstrument(Client client, string symbol, Instrument.Types.PRODUCT product = Instrument.Types.PRODUCT.Fut, Exchange.Types.EXCHANGE exch = Exchange.Types.EXCHANGE.NonExch, string targetMaturiyDate = "", BROKER broker = BROKER.Xceder)
        {
            var requestTask = client.queryInstrument(symbol, product, exch, targetMaturiyDate, broker);

            setLastSentRequestExpectation(false, Request.RequestOneofCase.QueryRequest);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            var instruments = requestTask.Result.Item2.QueryResult.Instruments.Instrument;

            foreach (var i in instruments)
            {
                Assert.AreEqual(symbol, i.Identity.Symbol);

                if (product != Instrument.Types.PRODUCT.UnknownType)
                    Assert.AreEqual(product, i.Product);

                if (broker != BROKER.Xceder)
                    Assert.AreEqual(broker, i.Identity.Broker);

                if (exch != Exchange.Types.EXCHANGE.NonExch)
                    Assert.AreEqual(exch, i.Identity.Exchange);
            }

            return instruments;
        }

        private XcederStream connectToServer()
        {
            string server = "gatewaydev.xceder.com";

            var stream = new XcederStream();

            stream.ResponseStream.Subscribe(onResponse);
            stream.NetworkErrorStream.Subscribe(onNetworkError);

            var task = stream.XcederClient.connect(server, 81);

            try
            {
                task.Wait();
            }
            catch (Exception)
            {

            }

            Assert.AreEqual(task.Status, TaskStatus.Faulted);

            task = stream.XcederClient.connect(server, 8082);

            task.Wait();

            Assert.IsTrue(task.Result);

            return stream;
        }

        private XcederStream login(string userID = "", string pwd = "")
        {
            var stream = connectToServer();

            var client = stream.XcederClient;

            if (string.IsNullOrWhiteSpace(userID))
                userID = "testUser" + DateTime.Now;

            if (string.IsNullOrWhiteSpace(pwd))
                pwd = "testUser";

            var requestTask = client.login(userID, "test");

            setLastSentRequestExpectation(false, Request.RequestOneofCase.Logon, ERRORCODE.InvalidUserid);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            requestTask = client.registerAccount(userID, userID + "@fake.com", pwd);

            setLastSentRequestExpectation(false, Request.RequestOneofCase.Account, ERRORCODE.Success);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            requestTask = client.login(userID, "pwd");

            setLastSentRequestExpectation(false, Request.RequestOneofCase.Logon, ERRORCODE.WrongPassword);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            requestTask = client.login(userID, pwd);

            setLastSentRequestExpectation(false, Request.RequestOneofCase.Logon);

            requestTask.Wait();

            assertSendRequestResult(requestTask);

            return stream;
        }

        private void waitResponse(int timeoutLoop = 0)
        {
            isResponded = false;

            while (!isResponded && timeoutLoop != 0)
            {
                timeoutLoop--;

                Thread.Sleep(1000);
            }
        }

        private void setLastSentRequestExpectation(bool hasError, Request.RequestOneofCase requestType, ERRORCODE resultCode = ERRORCODE.Success)
        {
            lastSentHasError = hasError;
            lastSentRequestType = requestType;
            lastSentRequestResultCode = resultCode;
        }

        private void assertSendRequestResult(Task<Tuple<Request, Response>> task)
        {
            if (lastSentHasError)
            {
                Assert.IsTrue(task.IsFaulted);
            }
            else
            {
                var pair = task.Result;

                var request = pair.Item1;
                var response = pair.Item2;

                Assert.AreEqual(lastSentRequestType, request.RequestCase);
                Assert.AreEqual(lastSentRequestResultCode, response.Result.ResultCode);
                Assert.AreEqual(request.RequestID, response.Result.Request);
            }
        }

        private void onNetworkError(Exception ex)
        {
            Debug.WriteLine("recieve network error:{0}\n{1}", ex.Message, ex.StackTrace);
        }

        private void onResponse(Response response)
        {
            switch (response.ResponseCase)
            {
                case Response.ResponseOneofCase.OrderReports:
                    OrderReports orderReports = response.OrderReports;

                    foreach (var orderReport in orderReports.OrderReport)
                    {
                        Debug.WriteLine("recieve the order/exeuction report message:{0}", orderReport);
                    }
                    break;

                case Response.ResponseOneofCase.MarketDatas:
                    MarketDatas marketDatas = response.MarketDatas;

                    foreach (var marketData in marketDatas.MarketData)
                    {
                        Debug.WriteLine("recieve the market data update message:{0}", marketData);
                    }

                    break;

                case Response.ResponseOneofCase.Ping:
                    Ping pingMsg = response.Ping;

                    Debug.WriteLine("recieve the ping message:{0}", pingMsg);
                    break;

                case Response.ResponseOneofCase.Notice:
                    NoticeMessages noticeMsgs = response.Notice;

                    foreach (var noticeMsg in noticeMsgs.Notice)
                    {
                        Debug.WriteLine("recieve the notice message:{0}", noticeMsg);
                    }
                    break;

                case Response.ResponseOneofCase.ServiceStatus:
                    ServiceStatuses serviceStatuses = response.ServiceStatus;

                    foreach (var status in serviceStatuses.Status)
                    {
                        Debug.WriteLine("recieve the service status message:{0}", status);
                    }
                    break;

                case Response.ResponseOneofCase.Instruments:
                    Instruments instruments = response.Instruments;

                    foreach (var instrument in instruments.Instrument)
                    {
                        Debug.WriteLine("recieve the new instrument message:{0}", instrument);
                    }
                    break;

                case Response.ResponseOneofCase.InstrumentStatus:
                    InstrumentStatuses instrumentStatuses = response.InstrumentStatus;

                    foreach (var instrumentStatus in instrumentStatuses.Status)
                    {
                        Debug.WriteLine("recieve the instrument status change message:{0}", instrumentStatus);
                    }
                    break;

                case Response.ResponseOneofCase.SpreaderID:
                case Response.ResponseOneofCase.LogonResult:
                case Response.ResponseOneofCase.QueryResult:
                case Response.ResponseOneofCase.None:
                case Response.ResponseOneofCase.Extension:
                default:
                    Assert.Fail("this should not in broadcast message from server");
                    break;
            }
        }
    }
}
