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

        private Account _accountInfo = new Account();

        private static DateTime EPOCH = new DateTime(1970, 1, 1);

        /// <summary>
        /// provide the message receive event. if the message isn't reply for the sent request, the Request will be null 
        /// </summary>
        public event EventHandler<Tuple<Request, Response>> RcvMessageEvent;

        /// <summary>
        /// error event for the error encountered when try to receive the message from the socket
        /// </summary>
        public event EventHandler<Exception> RcvErrorEvent;

        /// <summary>
        /// after the message is sent, this event will be triggered with the sent message and exception if any. If it is successful sent out, the exception will be null
        /// </summary>       
        public event EventHandler<Tuple<Request, Exception>> PostSendMessageEvent;

        private long sentRequestID = (long)DateTime.UtcNow.TimeOfDay.TotalSeconds;

        /// <summary>
        /// store the sent request and will be retrieved to trigger the received message event 
        /// </summary>
        private ConcurrentDictionary<ulong, Request> _sentRequests = new ConcurrentDictionary<ulong, Request>();

        private readonly object _lock = new object();

        private const int CONNECT_CHECK_PERIOD = 60 * 1000;

        private DateTime LastRcvMsgTime { get; set; }

        private Socket connectedSocket = null;

        private MemoryStream protoStream = new MemoryStream(BUFFER_SIZE);

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
            get { return _accountInfo; }
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

                sendPingRequest();
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
            //eventArgs.SetBuffer(new byte[BUFFER_SIZE], 0, BUFFER_SIZE);

            if (!s.ConnectAsync(eventArgs))
                onSocketComplete(s, eventArgs);
        }

        private void onSocketComplete(object sender, SocketAsyncEventArgs e)
        {
            Action<SocketAsyncEventArgs> handler;

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

                    connectedSocket.ReceiveAsync(eventArgs);
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
                        processRcvBytes(asyncArgs);
                    }
                    else
                    {
                        throw new SocketException((int)asyncArgs.SocketError);
                    }
                }
                catch (Exception e)
                {
                    RcvErrorEvent?.Invoke(this, e);
                }
            }
        }

        private void processRcvBytes(SocketAsyncEventArgs asyncArgs)
        {
            int bytesRead = asyncArgs.BytesTransferred;

            if (bytesRead > 0)
            {
                List<Tuple<Request, Response>> messageList;

                LastRcvMsgTime = DateTime.Now;

                // There might be more data, so store the data received so far.
                for (int index = 0; index < bytesRead; index++)
                {
                    _socketRcvBuffer.PushBack(asyncArgs.Buffer[index]);
                }

                //time to parse the message
                messageList = processRcvBytes();

                messageList.ForEach(t =>
                {
                    notifyRcvProtoMsg(t);
                });
            }

            // continue to receive the data
            if (bytesRead > 0)
            {
                asyncArgs.SetBuffer(0, BUFFER_SIZE);

                ((Socket)asyncArgs.UserToken).ReceiveAsync(asyncArgs);
            }
            else
            {
                throw new Exception("socket is closed because receiving bytes " + bytesRead.ToString() + " (should >= 0)");
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
        }

        private List<Tuple<Request, Response>> processRcvBytes()
        {
            List<Tuple<Request, Response>> messageList = new List<Tuple<Request, Response>>();

            Response msg = null;

            while (_socketRcvBuffer.Size > 0)
            {
                System.IO.Stream stream = new CircularBufferStream(_socketRcvBuffer);

                msg = extractMessages<Response>(stream);

                if (msg != null)
                {
                    _socketRcvBuffer.PopFront((int)stream.Position);

                    Request request = null;

                    if (msg.Result != null)
                    {
                        _sentRequests.TryRemove(msg.Result.Request, out request);
                    }

                    messageList.Add(Tuple.Create(request, msg));
                }
                else
                    break;
            }

            return messageList;
        }

        private void notifyRcvProtoMsg(Tuple<Request, Response> pair)
        {
            try
            {
                RcvMessageEvent?.Invoke(this, pair);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("encounter exception when rocess the recieved proto msg:" + pair.Item2.ToString() + " error:" + ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }

        private Request createRequestMsg()
        {
            Request request = new Request();

            request.RequestUTC = (ulong)(DateTime.UtcNow - EPOCH).TotalMilliseconds;

            request.RequestID = (ulong)System.Threading.Interlocked.Increment(ref sentRequestID);

            return request;
        }

        private void send(Request message)
        {
            ulong requestID = 0;

            Exception exception = null;

            try
            {
                lock (_lock)
                {
                    protoStream.Seek(0, SeekOrigin.Begin);

                    message.WriteDelimitedTo(protoStream);

                    byte[] data = protoStream.ToArray();

                    connectedSocket.Send(data);
                }

                Result result = new Result();

                result.ResultCode = ERRORCODE.Processing;
                result.Request = message.RequestID;

                requestID = message.RequestID;

                _sentRequests[requestID] = message;
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            PostSendMessageEvent?.Invoke(this, Tuple.Create(message, exception));
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
        public void sendLogin(string userID, string password)
        {
            Request request = new Request();

            var logonRequest = request.Logon = new Logon();

            request.RequestUTC = (ulong)(DateTime.UtcNow - EPOCH).TotalMilliseconds;

            password = Convert.ToBase64String((MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(password))));

            logonRequest.UserID = userID;
            logonRequest.Password = rsaEncrypt(request.RequestUTC.ToString() + "," + password);
            logonRequest.ClientApp = "C#";

            send(request);
        }

        private void sendLogOut(string reason)
        {
            Request request = new Request();

            request.Logoff = reason;

            send(request);
        }

        /// <summary>
        /// request to register a new account
        /// </summary>
        /// <param name="userID"></param>
        /// <param name="email"></param>
        /// <param name="password"></param>
        public void registerAccount(string userID, string email, string password)
        {
            Request request = createRequestMsg();

            var account = request.Account = new Account();
            var particular = request.Account.Particular = new Particular();

            account.UserID = userID;
            account.Password = rsaEncrypt(request.RequestUTC.ToString() + "," + password);
            particular.Email = email;

            send(request);
        }

        /// <summary>
        /// request an one time password for the password change
        /// </summary>
        /// <param name="email"></param>
        public void requestOTP(string email)
        {
            Request request = createRequestMsg();

            request.PasswordChange = new PasswordChange();

            request.PasswordChange.Email = email;

            send(request);
        }

        /// <summary>
        /// request password reset
        /// </summary>
        /// <param name="email"></param>
        /// <param name="OTP">one time password gotten by the requestOTP()</param>
        /// <param name="newPassword"></param>
        public void requestPWDReset(string email, string OTP, string newPassword)
        {
            Request request = createRequestMsg();

            var passwordChange = request.PasswordChange = new PasswordChange();

            newPassword = OTP + "," + rsaEncrypt(request.RequestUTC + "," + newPassword);

            passwordChange.Email = email;
            passwordChange.CurrentPassword = "";
            passwordChange.NewPassword = newPassword;

            send(request);
        }



        /// <summary>
        /// subscribe or unsubscribe the specified instrument IDs market data
        /// </summary>
        /// <param name="instrumentIDAry"></param>
        /// <param name="isUnSubscribe"></param>
        public void subscribePrice(ulong[] instrumentIDAry, bool isUnSubscribe = false)
        {
            Request request = createRequestMsg();

            var marketData = request.MarketData = new InstrumentSubscription();

            marketData.Instrument.AddRange(instrumentIDAry);

            marketData.Action = isUnSubscribe ? InstrumentSubscription.Types.ACTION.Unsubscribe : InstrumentSubscription.Types.ACTION.Subscribe;

            send(request);
        }

        /// <summary>
        /// send the ping request to the server to ensure the connection status
        /// </summary>
        public void sendPingRequest()
        {
            Request request = createRequestMsg();

            var ping = request.Ping = new Ping();

            ping.PingUTC = toEpochMilliseconds(DateTime.Now);

            send(request);
        }

        /// <summary>
        /// submit the order
        /// </summary>
        /// <param name="orderParams"></param>
        public void submitOrder(OrderParams orderParams)
        {
            Request request = createRequestMsg();

            Order order = new Order();

            order.Account = AccountInfo.AccountID;
            order.EnterBy = AccountInfo.AccountID;
            order.SubmitUTC = toEpochMilliseconds(DateTime.Now);

            request.Order = order;

            order.Params = orderParams;

            send(request);
        }

        /// <summary>
        /// replace/withdraw the target order
        /// </summary>
        /// <param name="targetClOrdID">target order clOrdID</param>
        /// <param name="newQty">new order qty, 0 means withdraw the target order</param>
        /// <param name="newLimitPrice"></param>
        /// <param name="newStopPrice"></param>
        public void replaceOrWithdrawOrder(ulong targetClOrdID, uint newQty, ulong newLimitPrice, ulong newStopPrice)
        {
            OrderParams orderParams = new OrderParams();

            orderParams.OrigClOrdID = targetClOrdID;
            orderParams.OrderQty = newQty;
            orderParams.LimitPrice = newLimitPrice;
            orderParams.StopPrice = newStopPrice;

            submitOrder(orderParams);
        }
    }
}
