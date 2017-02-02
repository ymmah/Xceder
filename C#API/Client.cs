using Google.Protobuf;
using System;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Com.Xceder.Messages;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Security.Cryptography;
using System.Reflection;

namespace Xceder
{
    class CircularBufferStream : Stream
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
        const int BUFFER_SIZE = 1024 * 1024;

        private CircularBuffer<byte> _socketRcvBuffer = new CircularBuffer<byte>(BUFFER_SIZE * 10);

        private Account _accountInfo = new Account();

        private static DateTime EPOCH = new DateTime(1970, 1, 1);

        /// <summary>
        /// provide the message receive event. if the message isn't reply for the sent request, the RequestMsg will be null 
        /// </summary>
        public event EventHandler<Tuple<RequestMsg, ResponseMsg>> RcvMessageEvent;

        /// <summary>
        /// after the message is sent successfully, this event will be triggered with the sent message
        /// </summary>
        public event EventHandler<RequestMsg> PostSendMessageEvent;

        /// <summary>
        /// error event for the error encountered 
        /// </summary>
        public event EventHandler<Exception> ErrorEvent;

        private long sentRequestID = (long)DateTime.UtcNow.TimeOfDay.TotalSeconds;

        //store all sent request and result from the server
        private ConcurrentDictionary<ulong, Tuple<RequestMsg, Result>> _sentRequests = new ConcurrentDictionary<ulong, Tuple<RequestMsg, Result>>();

        private readonly object _lock = new object();

        private const int CONNECT_CHECK_PERIOD = 60 * 1000;

        private DateTime LastRcvMsgTime { get; set; }
        private IDisposable _connectionCheckHandle = Disposable.Empty;

        private SocketAsyncEventArgs currentSocketAsyncArgs = null;

        private MemoryStream protoStream = new MemoryStream(BUFFER_SIZE);

        /// <summary>
        /// indicate whether the connection is started 
        /// </summary>
        public bool Started
        {
            get
            {
                return currentSocketAsyncArgs != null;
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
        private TMessage extractMessages<TMessage>(Stream stream) where TMessage : IMessage<TMessage>, new()
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

        /// <summary>
        /// return the sent request Result by the specified ID
        /// </summary>
        /// <param name="reqID">request ID</param>
        /// <returns>instance of Result</returns>
        public Result getRequestResult(ulong reqID)
        {
            Result result = null;
            Tuple<RequestMsg, Result> tuple = null;

            _sentRequests.TryGetValue(reqID, out tuple);

            if (tuple != null)
            {
                result = tuple.Item2;
            }

            return result;
        }

        private void checkConnection()
        {
            if (Started && (DateTime.Now - LastRcvMsgTime).TotalMilliseconds >= CONNECT_CHECK_PERIOD)
            {
                Debug.WriteLine("re-send login message for the idle connection ( idle >= " + (CONNECT_CHECK_PERIOD / 1000) + ") seconds");

                sendPingRequest();
            }
        }

        private void scheduleDeadConnectionCheck()
        {
            _connectionCheckHandle.Dispose();

            Debug.WriteLine("schedule dead connection check with the period=" + (CONNECT_CHECK_PERIOD / 1000) + "seconds");

            _connectionCheckHandle = Observable.Interval(TimeSpan.FromMilliseconds(CONNECT_CHECK_PERIOD)).Subscribe(p => checkConnection());
        }

        /// <summary>
        /// start to connect to the server
        /// </summary>
        /// <param name="server">server IP info</param>
        /// <param name="forceReconnect">indicate whether reconnect to the server if it is already connected</param>
        public void start(IPEndPoint server, bool forceReconnect = false)
        {
            if (!forceReconnect && Started && server.Equals(currentSocketAsyncArgs.RemoteEndPoint))
                return;

            stop("stop before start");

            try
            {
                lock (_lock)
                {
                    currentSocketAsyncArgs = connectAsync(server);

                    scheduleDeadConnectionCheck();
                }
            }
            catch (Exception ex)
            {
                ErrorEvent?.Invoke(this, ex);
            }
        }

        private SocketAsyncEventArgs connectAsync(IPEndPoint server)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();

            eventArgs.RemoteEndPoint = server;
            eventArgs.UserToken = s;
            eventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(onSocketComplete);
            eventArgs.SetBuffer(new byte[BUFFER_SIZE], 0, BUFFER_SIZE);
            s.ConnectAsync(currentSocketAsyncArgs);

            return eventArgs;
        }

        private void onSocketComplete(object sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    e.ConnectSocket.ReceiveAsync(e);
                    break;

                case SocketAsyncOperation.Receive:
                    onRcvMsg(e);
                    break;

                default:
                    break;
            }
        }

        private void onRcvMsg(SocketAsyncEventArgs asyncArgs)
        {
            try
            {
                if (!Started)
                    return;

                int bytesRead = asyncArgs.BytesTransferred;

                if (bytesRead > 0)
                {
                    List<Tuple<RequestMsg, ResponseMsg>> messageList;

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
                    asyncArgs.ConnectSocket.ReceiveAsync(asyncArgs);
                }
                else
                {
                    throw new Exception("socket is closed because receiving bytes " + bytesRead.ToString() + " (should >= 0)");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("when it is started=" + Started + ", recieve exception when process socket message:" + e.Message + System.Environment.NewLine + e.StackTrace);

                if (Started)
                    ErrorEvent?.Invoke(this, e);
            }
        }

        /// <summary>
        /// stop the current connection
        /// </summary>
        /// <param name="reason">sepcify the stop reason</param>
        public void stop(string reason)
        {
            lock (_lock)
            {
                try
                {
                    _connectionCheckHandle.Dispose();

                    if (currentSocketAsyncArgs != null && currentSocketAsyncArgs.ConnectSocket != null)
                    {
                        sendLogOut(reason);

                        currentSocketAsyncArgs.ConnectSocket.Shutdown(SocketShutdown.Both);
                        currentSocketAsyncArgs.ConnectSocket.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("encounter exception:" + ex.Message + Environment.NewLine + ex.StackTrace);
                }

                currentSocketAsyncArgs = null;
            }

            //discard those uncomplete received bytes
            while (!_socketRcvBuffer.IsEmpty)
                _socketRcvBuffer.PopFront(_socketRcvBuffer.Size);
        }

        private List<Tuple<RequestMsg, ResponseMsg>> processRcvBytes()
        {
            List<Tuple<RequestMsg, ResponseMsg>> messageList = new List<Tuple<RequestMsg, ResponseMsg>>();

            ResponseMsg msg = null;

            while (_socketRcvBuffer.Size > 0)
            {
                Stream stream = new CircularBufferStream(_socketRcvBuffer);

                msg = extractMessages<ResponseMsg>(stream);

                if (msg != null)
                {
                    _socketRcvBuffer.PopFront((int)stream.Position);

                    RequestMsg request = null;

                    if (msg.Result != null)
                    {
                        Tuple<RequestMsg, Result> tuple = null;

                        _sentRequests.TryGetValue(msg.Result.Request, out tuple);

                        if (tuple != null)
                        {
                            request = tuple.Item1;
                            _sentRequests[msg.Result.Request] = Tuple.Create(tuple.Item1, msg.Result);
                        }
                    }

                    messageList.Add(Tuple.Create(request, msg));
                }
                else
                    break;
            }

            return messageList;
        }

        private void notifyRcvProtoMsg(Tuple<RequestMsg, ResponseMsg> pair)
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

        private RequestMsg createRequestMsg()
        {
            RequestMsg request = new RequestMsg();

            request.RequestUTC = (ulong)(DateTime.UtcNow - EPOCH).TotalMilliseconds;

            request.RequestID = (ulong)System.Threading.Interlocked.Increment(ref sentRequestID);

            return request;
        }

        //<exception cref="Exception"></exception>
        private ulong send(RequestMsg message)
        {
            ulong requestID = 0;

            try
            {
                lock (_lock)
                {
                    protoStream.Seek(0, SeekOrigin.Begin);

                    message.WriteDelimitedTo(protoStream);

                    byte[] data = protoStream.ToArray();

                    Socket socket = currentSocketAsyncArgs.ConnectSocket;

                    socket.Send(data);
                }

                Result result = new Result();

                result.ResultCode = ERRORCODE.Processing;
                result.Request = message.RequestID;

                requestID = message.RequestID;

                _sentRequests[requestID] = Tuple.Create(message, result);

                PostSendMessageEvent?.Invoke(this, message);
            }
            catch (Exception ex)
            {
                ErrorEvent?.Invoke(this, ex);

                requestID = 0;
            }

            return requestID;
        }

        private RSA loadPublicKey()
        {
            RSA rsa = null;

            string key;

            var assembly = GetType().GetTypeInfo().Assembly;

            Stream resource = assembly.GetManifestResourceStream("xcederRSA.pub");

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
        /// <returns></returns>
        public ulong sendLogin(string userID, string password)
        {
            RequestMsg request = new RequestMsg();

            var logonRequest = request.Logon = new Logon();

            request.RequestUTC = (ulong)(DateTime.UtcNow - EPOCH).TotalMilliseconds;

            password = Convert.ToBase64String((MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(password))));

            logonRequest.UserID = userID;
            logonRequest.Password = rsaEncrypt(request.RequestUTC.ToString() + "," + password);
            logonRequest.ClientApp = "C#";

            return send(request);
        }

        private ulong sendLogOut(string reason)
        {
            RequestMsg request = new RequestMsg();

            request.Logoff = reason;

            return send(request);
        }

        /// <summary>
        /// request to register a new account
        /// </summary>
        /// <param name="userID"></param>
        /// <param name="email"></param>
        /// <param name="password"></param>
        /// <returns>0 means fail, otherwise the sent request ID for this request</returns>
        public ulong registerAccount(string userID, string email, string password)
        {
            RequestMsg request = createRequestMsg();

            var account = request.Account = new Account();
            var particular = request.Account.Particular = new Particular();

            account.UserID = userID;
            account.Password = rsaEncrypt(request.RequestUTC.ToString() + "," + password);
            particular.Email = email;

            return send(request);
        }

        /// <summary>
        /// request an one time password for the password change
        /// </summary>
        /// <param name="email"></param>
        /// <returns>0 means fail, otherwise the sent request ID for this request</returns>
        public ulong requestOTP(string email)
        {
            RequestMsg request = createRequestMsg();

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
        /// <returns>0 means fail, otherwise the sent request ID for this request</returns>
        public ulong requestPWDReset(string email, string OTP, string newPassword)
        {
            RequestMsg request = createRequestMsg();

            var passwordChange = request.PasswordChange = new PasswordChange();

            newPassword = OTP + "," + rsaEncrypt(request.RequestUTC + "," + newPassword);

            passwordChange.Email = email;
            passwordChange.CurrentPassword = "";
            passwordChange.NewPassword = newPassword;

            return send(request);
        }



        /// <summary>
        /// subscribe or unsubscribe the specified instrument IDs market data
        /// </summary>
        /// <param name="instrumentIDAry"></param>
        /// <param name="isUnSubscribe"></param>
        /// <returns>0 means fail, otherwise the sent request ID for this request</returns>
        public ulong subscribePrice(ulong[] instrumentIDAry, bool isUnSubscribe = false)
        {
            RequestMsg request = createRequestMsg();

            var marketData = request.MarketData = new InstrumentSubscription();

            marketData.Instrument.AddRange(instrumentIDAry);

            marketData.Action = isUnSubscribe ? InstrumentSubscription.Types.ACTION.Unsubscribe : InstrumentSubscription.Types.ACTION.Subscribe;

            return send(request);
        }

        /// <summary>
        /// send the ping request to the server to ensure the connection status
        /// </summary>
        /// <returns>0 means fail, otherwise the sent request ID for this request</returns>
        public void sendPingRequest()
        {
            RequestMsg request = createRequestMsg();

            var ping = request.Ping = new Ping();

            ping.PingUTC = toEpochMilliseconds(DateTime.Now);

            send(request);
        }

        /// <summary>
        /// submit the order
        /// </summary>
        /// <param name="orderParams"></param>
        /// <returns>0 means fail, otherwise the sent request ID for this order</returns>
        public ulong submitOrder(OrderParams orderParams)
        {
            RequestMsg request = createRequestMsg();

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
        /// <returns></returns>
        public ulong replaceOrWithdrawOrder(ulong targetClOrdID, uint newQty, ulong newLimitPrice, ulong newStopPrice)
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
