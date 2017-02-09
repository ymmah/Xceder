using Google.Protobuf;
using System;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Com.Xceder.Messages;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading.Tasks;

namespace Xceder
{
    class CircularBufferStream : System.IO.Stream
    {
        private CircularBuffer<byte> proxied;
        private int position = 0;
        private int maxBytes = -1;

        public CircularBufferStream(CircularBuffer<byte> proxied) : this(proxied, -1)
        {

        }

        public CircularBufferStream(CircularBuffer<byte> proxied, int range)
        {
            this.proxied = proxied;
            this.maxBytes = range;
        }

        private int getMaxBytePos()
        {
            int ret;

            if (maxBytes >= 0 && maxBytes < proxied.Size)
                ret = maxBytes;
            else
                ret = proxied.Size;

            return ret;
        }

        public override bool CanRead
        {
            get
            {
                return true;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return true;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return false;
            }
        }

        public override long Length
        {
            get
            {
                return getMaxBytePos();
            }
        }

        public override long Position
        {
            get
            {
                return position;
            }

            set
            {
                position = (int)value;
            }
        }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalLength = (int)this.Length;

            if (totalLength < count)
                count = totalLength;

            int bytesRead = 0;

            while (bytesRead < count && position < totalLength)
            {
                buffer[offset + bytesRead] = proxied[position++];
                bytesRead++;
            }

            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = (int)offset;
                    break;

                case SeekOrigin.Current:
                    position = position + (int)offset;
                    break;

                case SeekOrigin.End:
                    position = getMaxBytePos() + (int)offset;
                    break;
            }

            return position;
        }

        public override void SetLength(long value)
        {
            maxBytes = (int)value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }

    /**
     * <summary>handle the xceder server connection</summary>
     */
    public class Client
    {
        private const int BUFFER_SIZE = 1024 * 1024;

        private CircularBuffer<byte> _socketRcvBuffer = new CircularBuffer<byte>(BUFFER_SIZE * 10);

        private static DateTime EPOCH = new DateTime(1970, 1, 1);

        /// <summary>
        /// provide the server broadcast event.For example, the real time market data update
        /// </summary>
        public event EventHandler<Response> ResponseEvent;

        /// <summary>
        /// error event for the error encountered when try to receive the message from the socket
        /// </summary>
        public event EventHandler<Exception> NetWorkErrorEvent;

        private long sentRequestID = 0;

        /// <summary>
        /// store the sent request and will be retrieved to trigger the received message event 
        /// </summary>
        private ConcurrentDictionary<ulong, Tuple<Request, TaskCompletionSource<Tuple<Request, Response>>>> _sentRequests = new ConcurrentDictionary<ulong, Tuple<Request, TaskCompletionSource<Tuple<Request, Response>>>>();

        private readonly object _lock = new object();

        private const int CONNECT_CHECK_PERIOD = 60 * 1000;

        private DateTime LastRcvMsgTime { get; set; }

        private Socket connectedSocket = null;

        private readonly System.Threading.Timer timer;

        private readonly Dictionary<SocketAsyncOperation, Action<SocketAsyncEventArgs>> socketCompleteHandlerMap = new Dictionary<SocketAsyncOperation, Action<SocketAsyncEventArgs>>();

        /// <summary>
        /// Client constructor
        /// </summary>
        public Client()
        {
            timer = new System.Threading.Timer(onTimer, this, CONNECT_CHECK_PERIOD, CONNECT_CHECK_PERIOD);

            socketCompleteHandlerMap.Add(SocketAsyncOperation.Connect, onConnectComplete);
            socketCompleteHandlerMap.Add(SocketAsyncOperation.Receive, onRcvMsgComplete);

            AccountInfo = new Account();
        }

        /// <summary>
        /// indicate whether the server is connected 
        /// </summary>
        public bool Connected
        {
            get
            {
                return connectedSocket != null;
            }
        }

        /// <summary>
        /// convert the long UTC milliseconds into DateTime object
        /// </summary>
        /// <param name="UTCMillis">target UTC milliseconds</param>
        /// <returns>return a DateTime object</returns>
        public static DateTime convertUTC(ulong UTCMillis)
        {
            return EPOCH.AddMilliseconds(UTCMillis).ToLocalTime();
        }

        /// <summary>
        /// convert the local DateTime object into the UTC milliseconds
        /// </summary>
        /// <param name="localTime">target local datetime object</param>
        /// <returns>UTC milliseconds</returns>
        public static ulong toEpochMilliseconds(DateTime localTime)
        {
            return (ulong)(localTime.ToUniversalTime() - EPOCH).TotalMilliseconds;
        }

        /// <summary>
        /// last sent request ID
        /// </summary>
        public long LastSentRequestID
        {
            get { return sentRequestID; }
        }

        /// <summary>
        /// current account info. if it is not login yet, it is the default Account instance
        /// </summary>
        public Account AccountInfo
        {
            get; private set;
        }

        ///<exception cref="Exception"></exception>
        private TMessage extractMessages<TMessage>(System.IO.Stream stream) where TMessage : IMessage<TMessage>, new()
        {
            TMessage result = default(TMessage);

            try
            {
                CodedInputStream protoStream = new CodedInputStream(stream);

                var msgBytesLength = protoStream.ReadLength() + protoStream.Position;

                if (stream.Length >= msgBytesLength)
                {
                    var builder = new TMessage();

                    stream.SetLength(msgBytesLength);

                    stream.Seek(protoStream.Position, SeekOrigin.Begin);

                    builder.MergeFrom(stream);

                    result = builder;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("fail to extract the proto message:" + ex.Message + Environment.NewLine + ex.StackTrace);
            }

            return result;
        }

        /// <summary>
        /// check whether the specified request by the ID is sent successfully
        /// </summary>
        /// <param name="reqID">requet ID</param>
        /// <returns>true: sent out successfully, false: not sent</returns>
        public bool isRequestSent(ulong reqID)
        {
            return _sentRequests.ContainsKey(reqID);
        }

        private void onTimer(object state)
        {
            if (Connected && (DateTime.Now - LastRcvMsgTime).TotalMilliseconds >= CONNECT_CHECK_PERIOD)
            {
                Debug.WriteLine("send ping message for the idle connection ( idle >= " + (CONNECT_CHECK_PERIOD / 1000) + ") seconds");

                pingServer();
            }
        }

        /// <summary>
        /// start to connect to the server
        /// </summary>
        /// <param name="server">server IP or domain</param>
        /// <param name="port">port number</param>
        /// <returns>Task to wait on the async result, true means connected, false means fail</returns>
        public Task<bool> connect(string server, int port)
        {
            close("close before start");

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                var serverAddr = Dns.GetHostAddressesAsync(server).Result[0];

                lock (_lock)
                {
                    connectAsync(new IPEndPoint(serverAddr, port), tcs);
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        private void connectAsync(IPEndPoint server, TaskCompletionSource<bool> tcs)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();

            eventArgs.RemoteEndPoint = server;
            eventArgs.UserToken = tcs;
            eventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(onSocketComplete);

            if (!s.ConnectAsync(eventArgs))
                onSocketComplete(s, eventArgs);
        }

        private void onSocketComplete(object sender, SocketAsyncEventArgs e)
        {
            Action<SocketAsyncEventArgs> handler;

            Debug.WriteLine(e.LastOperation + " is completed");

            if (socketCompleteHandlerMap.TryGetValue(e.LastOperation, out handler))
            {
                handler.Invoke(e);
            }
        }

        private void onConnectComplete(SocketAsyncEventArgs e)
        {
            bool isSuccess = e.SocketError == SocketError.Success;

            var tcs = (TaskCompletionSource<bool>)e.UserToken;

            if (isSuccess)
            {
                tcs.SetResult(true);

                lock (_lock)
                {
                    connectedSocket = e.ConnectSocket;

                    SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();

                    eventArgs.UserToken = connectedSocket;
                    eventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(onSocketComplete);
                    eventArgs.SetBuffer(new byte[BUFFER_SIZE], 0, BUFFER_SIZE);

                    if (!connectedSocket.ReceiveAsync(eventArgs))
                        onSocketComplete(connectedSocket, e);
                }
            }
            else
            {
                tcs.SetException(new SocketException((int)e.SocketError));
            }
        }

        private void onRcvMsgComplete(SocketAsyncEventArgs asyncArgs)
        {
            if (Connected)
            {
                try
                {
                    bool isSuccess = asyncArgs.SocketError == SocketError.Success;

                    if (isSuccess)
                    {
                        int bytesRead = asyncArgs.BytesTransferred;

                        if (bytesRead > 0)
                        {
                            saveToBuffer(asyncArgs.Buffer, bytesRead);

                            // continue to receive the data
                            asyncArgs.SetBuffer(0, BUFFER_SIZE);

                            var socket = (Socket)asyncArgs.UserToken;
                            if (!socket.ReceiveAsync(asyncArgs))
                                onSocketComplete(socket, asyncArgs);
                            else
                            {
                                //time to parse the message
                                processBuffer();
                            }
                        }
                        else
                        {
                            throw new Exception("socket is closed because receiving bytes " + bytesRead.ToString() + " (should >= 0)");
                        }
                    }
                    else
                    {
                        throw new SocketException((int)asyncArgs.SocketError);
                    }
                }
                catch (Exception e)
                {
                    NetWorkErrorEvent?.Invoke(this, e);
                }
            }
        }

        private void saveToBuffer(byte[] rcvBytes, int bytesRead)
        {
            LastRcvMsgTime = DateTime.Now;

            // There might be more data, so store the data received so far.
            for (int index = 0; index < bytesRead; index++)
            {
                _socketRcvBuffer.PushBack(rcvBytes[index]);
            }
        }

        /// <summary>
        /// stop the current connection
        /// </summary>
        /// <param name="reason">sepcify the stop reason</param>
        public void close(string reason)
        {
            lock (_lock)
            {
                try
                {
                    if (connectedSocket != null)
                    {
                        sendLogOut(reason);

                        connectedSocket.Shutdown(SocketShutdown.Both);
                        connectedSocket.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("encounter exception:" + ex.Message + Environment.NewLine + ex.StackTrace);
                }

                connectedSocket = null;
            }

            //discard those uncomplete received bytes
            while (!_socketRcvBuffer.IsEmpty)
                _socketRcvBuffer.PopFront(_socketRcvBuffer.Size);

            AccountInfo = new Account();
        }

        private void processBuffer()
        {
            List<Response> messageList = new List<Response>();

            Response msg = null;

            while (_socketRcvBuffer.Size > 0)
            {
                System.IO.Stream stream = new CircularBufferStream(_socketRcvBuffer);

                msg = extractMessages<Response>(stream);

                if (msg != null)
                {
                    _socketRcvBuffer.PopFront((int)stream.Position);

                    Tuple<Request, TaskCompletionSource<Tuple<Request, Response>>> requestPair = null;

                    Tuple<Request, Response> responsePair;

                    if (msg.Result != null)
                    {
                        _sentRequests.TryRemove(msg.Result.Request, out requestPair);
                    }

                    if (requestPair != null)
                    {
                        Request request = requestPair.Item1;

                        responsePair = Tuple.Create(request, msg);

                        //request reponse will be emited through Task
                        requestPair.Item2.SetResult(responsePair);
                    }
                    else
                        broadcastProtoMsg(msg);
                }
                else
                    break;
            }
        }

        private void broadcastProtoMsg(Response response)
        {
            try
            {
                ResponseEvent?.Invoke(this, response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("encounter exception when rocess the recieved proto msg:" + response.ToString() + " error:" + ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }

        private Request createRequestMsg()
        {
            Request request = new Request();

            request.RequestUTC = (ulong)(DateTime.UtcNow - EPOCH).TotalMilliseconds;

            request.RequestID = (ulong)System.Threading.Interlocked.Increment(ref sentRequestID);

            return request;
        }

        private Task<Tuple<Request, Response>> send(Request message)
        {
            Exception exception = null;

            var tcs = new TaskCompletionSource<Tuple<Request, Response>>();

            try
            {
                MemoryStream protoStream = new MemoryStream(BUFFER_SIZE);

                lock (_lock)
                {
                    message.WriteDelimitedTo(protoStream);

                    byte[] data = protoStream.ToArray();

                    connectedSocket.Send(data);
                }

                _sentRequests[message.RequestID] = Tuple.Create(message, tcs);
            }
            catch (Exception ex)
            {
                exception = ex;

                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        private RSA loadPublicKey()
        {
            RSA rsa = null;

            string key;

            var assembly = GetType().GetTypeInfo().Assembly;

            System.IO.Stream resource = assembly.GetManifestResourceStream("Xceder.xcederRSA.pub");

            StreamReader streamReader = new StreamReader(resource);

            key = streamReader.ReadToEnd();

            var pemkey = PemKeyUtils.DecodeOpenSSLPublicKey(key);

            RSAParameters? rsaParam = PemKeyUtils.DecodeX509PublicKey(pemkey);

            if (rsaParam != null)
            {
                rsa = RSA.Create();

                rsa.ImportParameters((RSAParameters)rsaParam);
            }

            return rsa;
        }


        private string rsaEncrypt(string val)
        {
            string result = "";

            byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(val);

            RSA rsa = loadPublicKey();

            if (rsa != null)
                result = Convert.ToBase64String(loadPublicKey().Encrypt(dataBytes, RSAEncryptionPadding.Pkcs1));

            return result;
        }

        /// <summary>
        /// send login request to the server. 
        /// </summary>
        /// <param name="userID"></param>
        /// <param name="password"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> login(string userID, string password)
        {
            Request request = createRequestMsg();

            var logonRequest = request.Logon = new Logon();

            request.RequestUTC = (ulong)(DateTime.UtcNow - EPOCH).TotalMilliseconds;

            password = Convert.ToBase64String((MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(password))));

            logonRequest.UserID = userID;
            logonRequest.Password = rsaEncrypt(request.RequestUTC.ToString() + "," + password);
            logonRequest.ClientApp = "C#";

            return send(request);
        }

        private void sendLogOut(string reason)
        {
            Request request = createRequestMsg();

            request.Logoff = reason;

            send(request);
        }

        /// <summary>
        /// request to register a new account
        /// </summary>
        /// <param name="userID"></param>
        /// <param name="email"></param>
        /// <param name="password"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> registerAccount(string userID, string email, string password)
        {
            Request request = createRequestMsg();

            var account = request.Account = new Account();
            var particular = request.Account.Particular = new Particular();

            password = Convert.ToBase64String((MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(password))));

            account.UserID = userID;
            account.Password = rsaEncrypt(request.RequestUTC.ToString() + "," + password);
            particular.Email = email;

            return send(request);
        }

        /// <summary>
        /// request an one time password for the password change
        /// </summary>
        /// <param name="email"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> requestOTP(string email)
        {
            Request request = createRequestMsg();

            request.PasswordChange = new PasswordChange();

            request.PasswordChange.Email = email;

            return send(request);
        }

        /// <summary>
        /// request password reset
        /// </summary>
        /// <param name="email"></param>
        /// <param name="OTP">one time password gotten by the requestOTP()</param>
        /// <param name="newPassword"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> requestPWDReset(string email, string OTP, string newPassword)
        {
            Request request = createRequestMsg();

            var passwordChange = request.PasswordChange = new PasswordChange();

            newPassword = OTP + "," + rsaEncrypt(request.RequestUTC + "," + newPassword);

            passwordChange.Email = email;
            passwordChange.CurrentPassword = "";
            passwordChange.NewPassword = newPassword;

            return send(request);
        }

        /// <summary>
        /// query the instrument info
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="product">target product, default value "UnknownType" means query all products</param>
        /// <param name="exchange">target exchange only, default value "NonExch" means query all exchanges</param>
        /// <param name="broker">target broker only, default value "Xceder" means all brokers</param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> queryInstrument(string symbol, Instrument.Types.PRODUCT product = Instrument.Types.PRODUCT.UnknownType, Exchange.Types.EXCHANGE exchange = Exchange.Types.EXCHANGE.NonExch, BROKER broker = BROKER.Xceder)
        {
            Request request = createRequestMsg();

            var query = request.QueryRequest = new Query();

            var conditions = query.Instruments = new QueryConditions();

            conditions.Broker = broker;
            conditions.Exchange = exchange;
            conditions.Symbol = symbol;
            conditions.Product = product;

            return send(request);
        }

        /// <summary>
        /// subscribe or unsubscribe the specified instrument IDs market data
        /// </summary>
        /// <param name="instrumentIDAry"></param>
        /// <param name="isUnSubscribe"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> subscribePrice(ulong[] instrumentIDAry, bool isUnSubscribe = false)
        {
            Request request = createRequestMsg();

            var marketData = request.MarketData = new InstrumentSubscription();

            marketData.Instrument.AddRange(instrumentIDAry);

            marketData.Action = isUnSubscribe ? InstrumentSubscription.Types.ACTION.Unsubscribe : InstrumentSubscription.Types.ACTION.Subscribe;

            return send(request);
        }

        /// <summary>
        /// subscribe one instrument market data
        /// </summary>
        /// <param name="instrumentID"></param>
        /// <param name="isUnSubscribe"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> subscribePrice(ulong instrumentID, bool isUnSubscribe = false)
        {
            return subscribePrice(new ulong[] { instrumentID }, isUnSubscribe);
        }

        /// <summary>
        /// send the ping request to the server to ensure the connection status
        /// </summary>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> pingServer()
        {
            Request request = createRequestMsg();

            var ping = request.Ping = new Ping();

            ping.PingUTC = toEpochMilliseconds(DateTime.Now);

            return send(request);
        }

        /// <summary>
        /// submit the order
        /// </summary>
        /// <param name="orderParams"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> submitOrder(OrderParams orderParams)
        {
            Request request = createRequestMsg();

            Order order = new Order();

            order.Account = AccountInfo.AccountID;
            order.EnterBy = AccountInfo.AccountID;
            order.SubmitUTC = toEpochMilliseconds(DateTime.Now);

            request.Order = order;

            order.Params = orderParams;

            return send(request);
        }

        /// <summary>
        /// replace/withdraw the target order
        /// </summary>
        /// <param name="targetClOrdID">target order clOrdID</param>
        /// <param name="newQty">new order qty, 0 means withdraw the target order</param>
        /// <param name="newLimitPrice"></param>
        /// <param name="newStopPrice"></param>
        /// <returns>ansyc task result for this request</returns>
        public Task<Tuple<Request, Response>> replaceOrWithdrawOrder(ulong targetClOrdID, uint newQty, ulong newLimitPrice, ulong newStopPrice)
        {
            OrderParams orderParams = new OrderParams();

            orderParams.OrigClOrdID = targetClOrdID;
            orderParams.OrderQty = newQty;
            orderParams.LimitPrice = newLimitPrice;
            orderParams.StopPrice = newStopPrice;

            return submitOrder(orderParams);
        }
    }
}
